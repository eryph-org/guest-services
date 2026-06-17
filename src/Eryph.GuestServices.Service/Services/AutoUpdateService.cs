using System.Runtime.Versioning;
using Eryph.GuestServices.CloudConfig;
using Eryph.GuestServices.Core;
using Eryph.GuestServices.Provisioning.Update;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Eryph.GuestServices.Service.Services;

/// <summary>
/// Standalone background auto-patch loop: over the machine's lifetime it
/// periodically resolves + downloads + verifies the latest stable release and,
/// when newer, swaps the agent in-place via the same updater the provisioning
/// path uses. It runs for every long-running guest — remote-access-only AND
/// provisioned — so provisioned machines keep getting patched too.
/// </summary>
/// <remarks>
/// Windows-only (the self-updater stops/swaps a Windows service). The check
/// cadence is a random delay in [<see cref="MinInterval"/>, <see cref="MaxInterval"/>]
/// re-rolled each cycle, including before the first check. That multi-day jitter
/// does double duty: it spreads load across a fleet, and — because first-boot
/// provisioning completes in minutes — it guarantees a check never coincides
/// with the provisioning run, so auto-patch and provisioning never interfere.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class AutoUpdateService(
    IServiceControlFlags controlFlags,
    IEgsUpdater updater,
    IUpdateLauncher launcher,
    ILogger<AutoUpdateService> logger) : BackgroundService
{
    internal static readonly TimeSpan MinInterval = TimeSpan.FromHours(36);
    internal static readonly TimeSpan MaxInterval = TimeSpan.FromHours(48);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!controlFlags.IsAutoUpdateEnabled())
        {
            logger.LogInformation(
                "Auto-update disabled (AutoUpdateEnabled=0); no background self-update checks.");
            return;
        }

        logger.LogInformation(
            "Auto-update enabled; checking the stable channel every {Min}-{Max} hours.",
            MinInterval.TotalHours, MaxInterval.TotalHours);

        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = NextCheckDelay();
            logger.LogInformation("Next auto-update check in {Hours:F1} hours.", delay.TotalHours);
            try
            {
                await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            // An applied update restarts this service; the fresh process schedules
            // the next check, so stop looping once one is launched.
            if (await CheckOnceAsync(stoppingToken).ConfigureAwait(false))
                return;
        }
    }

    /// <summary>
    /// Runs one resolve→download→verify→stage→apply cycle. Returns true when an
    /// update was launched (the service is about to restart); false when nothing
    /// was applied (already current, no plan, or a transient failure — logged).
    /// </summary>
    internal async Task<bool> CheckOnceAsync(CancellationToken stoppingToken)
    {
        try
        {
            var plan = await updater.PrepareAsync(
                new EgsUpdateConfig { Enabled = true, Channel = UpdateTargetResolver.StableChannel },
                stoppingToken).ConfigureAwait(false);
            if (plan is null)
                return false;

            logger.LogInformation(
                "Auto-update: applying staged {Version}; the service will restart.", plan.TargetVersion);
            launcher.Launch(plan);
            return true;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Auto-update check failed; will retry at the next window.");
            return false;
        }
    }

    /// <summary>Uniform random delay in [<see cref="MinInterval"/>, <see cref="MaxInterval"/>].</summary>
    internal static TimeSpan NextCheckDelay() => NextCheckDelay(Random.Shared);

    internal static TimeSpan NextCheckDelay(Random random) =>
        MinInterval + (MaxInterval - MinInterval) * random.NextDouble();
}
