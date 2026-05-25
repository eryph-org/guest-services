using Eryph.GuestServices.Provisioning.Stages;
using Eryph.GuestServices.Provisioning.UserData;
using Eryph.GuestServices.Provisioning.Windows;
using Microsoft.Extensions.Logging;
using CloudConfigModel = Eryph.GuestServices.CloudConfig.CloudConfig;

namespace Eryph.GuestServices.Provisioning.Modules;

[Stage(Stage.Network, Frequency = ModuleFrequency.PerInstance)]
internal sealed class SetHostnameModule(ILogger<SetHostnameModule> logger) : IModule
{
    public async Task<ModuleOutcome> ApplyAsync(
        ResolvedUserData userData,
        IModuleContext context,
        CancellationToken cancellationToken)
    {
        var config = userData.CloudConfig;
        if (config.PreserveHostname == true)
        {
            logger.LogInformation("preserve_hostname is set; skipping hostname configuration.");
            return ModuleOutcome.Ok();
        }

        var desired = PickName(config);
        if (desired is null)
        {
            logger.LogDebug("No hostname or fqdn specified; nothing to do.");
            return ModuleOutcome.Ok();
        }

        var result = await context.Os.SetComputerNameAsync(desired, cancellationToken).ConfigureAwait(false);
        switch (result)
        {
            case SetComputerNameResult.AlreadySet:
                logger.LogInformation("Computer name is already '{Name}'.", desired);
                return ModuleOutcome.Ok();
            case SetComputerNameResult.SetWithRebootPending:
                logger.LogInformation("Computer name change to '{Name}' is pending reboot.", desired);
                return ModuleOutcome.Reboot($"Hostname change to '{desired}' requires reboot.");
            default:
                throw new InvalidOperationException($"Unexpected SetComputerNameResult: {result}");
        }
    }

    private static string? PickName(CloudConfigModel config)
    {
        // Cloud-init's prefer_fqdn_over_hostname swaps the precedence: when
        // true AND an fqdn is available, the fqdn wins over hostname.
        // Otherwise the hostname-first / fqdn-fallback default holds.
        var preferFqdn = config.PreferFqdnOverHostname == true;
        if (preferFqdn && !string.IsNullOrWhiteSpace(config.Fqdn))
            return ExtractNetBiosName(config.Fqdn);

        if (!string.IsNullOrWhiteSpace(config.Hostname))
            return config.Hostname.Trim();

        if (!string.IsNullOrWhiteSpace(config.Fqdn))
            return ExtractNetBiosName(config.Fqdn);

        return null;
    }

    private static string ExtractNetBiosName(string fqdn)
    {
        // The unqualified Windows computer name does not include the domain.
        var dot = fqdn.IndexOf('.');
        return dot > 0 ? fqdn[..dot] : fqdn;
    }
}
