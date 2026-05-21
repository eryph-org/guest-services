using Eryph.GuestServices.Provisioning.Stages;
using Eryph.GuestServices.Provisioning.Windows;
using Microsoft.Extensions.Logging;
using CloudConfigModel = Eryph.GuestServices.CloudConfig.CloudConfig;

namespace Eryph.GuestServices.Provisioning.Handlers;

[Stage(Stage.Hostname)]
internal sealed class SetHostnameHandler(ILogger<SetHostnameHandler> logger) : IHandler
{
    public async Task<HandlerOutcome> ApplyAsync(
        CloudConfigModel config,
        IHandlerContext context,
        CancellationToken cancellationToken)
    {
        if (config.PreserveHostname == true)
        {
            logger.LogInformation("preserve_hostname is set; skipping hostname configuration.");
            return HandlerOutcome.Ok();
        }

        var desired = PickName(config);
        if (desired is null)
        {
            logger.LogDebug("No hostname or fqdn specified; nothing to do.");
            return HandlerOutcome.Ok();
        }

        var result = await context.Os.SetComputerNameAsync(desired, cancellationToken).ConfigureAwait(false);
        switch (result)
        {
            case SetComputerNameResult.AlreadySet:
                logger.LogInformation("Computer name is already '{Name}'.", desired);
                return HandlerOutcome.Ok();
            case SetComputerNameResult.SetWithRebootPending:
                logger.LogInformation("Computer name change to '{Name}' is pending reboot.", desired);
                return HandlerOutcome.Reboot($"Hostname change to '{desired}' requires reboot.");
            default:
                throw new InvalidOperationException($"Unexpected SetComputerNameResult: {result}");
        }
    }

    private static string? PickName(CloudConfigModel config)
    {
        if (!string.IsNullOrWhiteSpace(config.Hostname))
            return config.Hostname.Trim();

        if (!string.IsNullOrWhiteSpace(config.Fqdn))
        {
            // Use the first label as the NetBIOS-style computer name. The
            // unqualified Windows computer name does not include the domain.
            var dot = config.Fqdn.IndexOf('.');
            return dot > 0 ? config.Fqdn[..dot] : config.Fqdn;
        }

        return null;
    }
}
