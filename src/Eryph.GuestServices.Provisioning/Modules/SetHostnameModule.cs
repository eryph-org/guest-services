using Eryph.GuestServices.Provisioning.Stages;
using Eryph.GuestServices.Provisioning.UserData;
using Eryph.GuestServices.Provisioning.Windows;
using Microsoft.Extensions.Logging;
using CloudConfigModel = Eryph.GuestServices.CloudConfig.CloudConfig;

namespace Eryph.GuestServices.Provisioning.Modules;

[Stage(Stage.Network, Frequency = ModuleFrequency.PerInstance)]
internal sealed class SetHostnameModule(ILogger<SetHostnameModule> logger) : IModule
{
    // The Windows NetBIOS computer name is capped at 15 characters. A longer
    // name silently truncates server-side, but Environment.MachineName then
    // reports the 15-char form — so the "already set" check in the OS layer
    // would never match a longer desired name, producing an endless
    // rename-reboot loop. cloudbase-init's set_hostname module truncates here
    // for the same reason; we mirror that behaviour.
    private const int NetBiosNameMaxLength = 15;

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

        if (desired.Length > NetBiosNameMaxLength)
        {
            var truncated = desired[..NetBiosNameMaxLength];
            logger.LogWarning(
                "Hostname '{Hostname}' exceeds the Windows NetBIOS limit of {Max} characters; truncating to '{Truncated}'.",
                desired, NetBiosNameMaxLength, truncated);
            desired = truncated;
        }

        // One-shot guard: we already requested a rename + reboot for this
        // instance. Don't compare-and-retry — any post-reboot mismatch
        // (length truncation, case folding, character drops) would otherwise
        // trigger another reboot, and so on until the per-module reboot cap
        // aborts provisioning. Accept whatever the OS settled on.
        if (context.IsRebootResume)
        {
            var actual = await context.Os.GetComputerNameAsync(cancellationToken).ConfigureAwait(false);
            if (string.Equals(actual, desired, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogInformation("Computer name is '{Name}' after reboot.", actual);
            }
            else
            {
                logger.LogWarning(
                    "Computer name is '{Actual}' after reboot; requested '{Desired}'. Accepting the OS-normalized value rather than triggering another rename.",
                    actual, desired);
            }
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
