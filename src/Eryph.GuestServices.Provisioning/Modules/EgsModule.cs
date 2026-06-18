using Eryph.GuestServices.CloudConfig;
using Eryph.GuestServices.Core;
using Eryph.GuestServices.Provisioning.Stages;
using Eryph.GuestServices.Provisioning.Update;
using Eryph.GuestServices.Provisioning.UserData;
using Microsoft.Extensions.Logging;

namespace Eryph.GuestServices.Provisioning.Modules;

/// <summary>
/// eryph extension (not cloud-init): applies the <c>egs:</c> block, which
/// configures the guest-services agent itself — its operator capability
/// switches (remote access / provisioning / KVP auth / port forwarding) and,
/// later, self-update.
/// </summary>
/// <remarks>
/// Runs last in the Network stage (after network-config, NTP, timezone and
/// locale) so networking is up for the self-update download, but before any
/// Config-stage module. That ordering matters for self-update: an update
/// restarts the agent, and the entire Config/Final pipeline then runs on the
/// new binary.
/// </remarks>
[Stage(Stage.Network, Order = 6, Frequency = ModuleFrequency.PerInstance)]
internal sealed class EgsModule(ILogger<EgsModule> logger, IEgsUpdater updater) : IModule
{
    public async Task<ModuleOutcome> ApplyAsync(
        ResolvedUserData userData,
        IModuleContext context,
        CancellationToken cancellationToken)
    {
        var egs = userData.CloudConfig.Egs;
        if (egs is null)
        {
            logger.LogDebug("No egs block; leaving agent configuration alone.");
            return ModuleOutcome.Ok();
        }

        // Self-update FIRST: a staged update restarts the agent, and the rest of
        // provisioning (settings here, then every Config/Final module) then runs
        // on the new binary. On the post-update re-entry the updater finds the
        // running version already matches the target and returns no plan, so we
        // fall through to settings.
        var plan = await updater.PrepareAsync(egs.Update, cancellationToken).ConfigureAwait(false);
        if (plan is not null)
        {
            return ModuleOutcome.Update(
                $"applying egs self-update to {plan.TargetVersion}",
                plan.StagingDirectory,
                plan.TargetVersion);
        }

        try
        {
            await ApplySettingsAsync(egs.Settings, context, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to apply egs settings.");
            return ModuleOutcome.Fail($"egs settings: {ex.Message}", ex);
        }

        return ModuleOutcome.Ok();
    }

    private async Task ApplySettingsAsync(
        EgsSettingsConfig? settings,
        IModuleContext context,
        CancellationToken cancellationToken)
    {
        if (settings is null)
        {
            logger.LogDebug("No egs.settings block; leaving capability switches alone.");
            return;
        }

        // Each switch is three-state: null leaves it untouched (cloud-init's
        // explicit-value / omitted convention). The flags are read at the next
        // service start, so the change is durable but not mid-run.
        await WriteFlagIfSetAsync(ServiceControlFlag.RemoteAccess, settings.RemoteAccess, context, cancellationToken)
            .ConfigureAwait(false);
        await WriteFlagIfSetAsync(ServiceControlFlag.Provisioning, settings.Provisioning, context, cancellationToken)
            .ConfigureAwait(false);
        await WriteFlagIfSetAsync(ServiceControlFlag.KvpAuth, settings.KvpAuth, context, cancellationToken)
            .ConfigureAwait(false);
        await WriteFlagIfSetAsync(ServiceControlFlag.PortForwarding, settings.PortForwarding, context, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task WriteFlagIfSetAsync(
        ServiceControlFlag flag,
        bool? value,
        IModuleContext context,
        CancellationToken cancellationToken)
    {
        if (value is null)
            return;

        logger.LogInformation(
            "egs.settings: setting {Flag} to {Value} (effective on next service start).",
            flag, value.Value);
        await context.Os.SetServiceControlFlagAsync(flag, value.Value, cancellationToken).ConfigureAwait(false);
    }
}
