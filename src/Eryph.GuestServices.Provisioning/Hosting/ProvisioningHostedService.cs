using System.Diagnostics;
using Eryph.GuestServices.Core;
using Eryph.GuestServices.Provisioning.Reporting;
using Eryph.GuestServices.Provisioning.Reporting.Events;
using Eryph.GuestServices.Provisioning.Stages;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Eryph.GuestServices.Provisioning.Hosting;

/// <summary>
/// Runs provisioning once at host startup and then stays idle for the lifetime
/// of the host. Unlike the previous standalone <c>egs-provisioning.exe</c>, this
/// hosted service does NOT stop the host on success — the embedding service
/// (<c>egs-service</c>) keeps running so the SSH server and other long-lived
/// responsibilities remain available. The only outcome that triggers a host
/// shutdown is <see cref="StageRunOutcome.RebootRequested"/>, which spawns
/// <c>shutdown.exe</c> and asks the host to stop cleanly.
/// </summary>
internal sealed class ProvisioningHostedService(
    IStageRunner runner,
    IHostApplicationLifetime lifetime,
    IServiceControlFlags controlFlags,
    IReportingDispatcher reporter,
    Update.IUpdateLauncher updateLauncher,
    ILogger<ProvisioningHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!controlFlags.IsProvisioningEnabled())
        {
            logger.LogInformation(
                "Provisioning disabled via registry (HKLM\\SOFTWARE\\eryph\\guest-services\\ProvisioningEnabled=0); skipping first-boot provisioning.");
            return;
        }

        StageRunOutcome outcome;
        try
        {
            outcome = await runner.RunAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled exception in provisioning stage runner");
            Environment.ExitCode = 1;
            // Surface the failure so KVP reports `failed` rather than staying
            // `running` forever. A swallowed crash here (e.g. a state-save replace
            // denied by AV) previously left provisioning indistinguishable from a
            // hung run. Reporting must not itself throw out of the catch — report
            // on a non-cancellable token and swallow any reporting error.
            try
            {
                await reporter.EmitAsync(
                    new ReportingEvent.ProvisioningFailed($"Unhandled exception: {ex.Message}", ex)
                    {
                        Origin = "provisioning-host",
                    },
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception reportEx)
            {
                logger.LogError(reportEx, "Failed to report provisioning failure state");
            }
            // Do NOT stop the host: egs-service keeps running its other
            // responsibilities (SSH server, host channel) even when provisioning
            // failed.
            return;
        }

        switch (outcome)
        {
            case StageRunOutcome.Success:
                logger.LogInformation("Provisioning completed successfully.");
                Environment.ExitCode = 0;
                return;

            case StageRunOutcome.NoDataSource:
                logger.LogInformation("No data source available; nothing to provision.");
                Environment.ExitCode = 0;
                return;

            case StageRunOutcome.RebootRequested reboot:
                logger.LogInformation("Provisioning reboot requested: {Reason}", reboot.Reason);
                TriggerReboot();
                Environment.ExitCode = 0;
                // Stop the host so egs-service shuts down cleanly before the
                // OS reboot fires.
                lifetime.StopApplication();
                return;

            case StageRunOutcome.UpdateRequested update:
                logger.LogInformation(
                    "Provisioning self-update requested to {Version}: {Reason}",
                    update.TargetVersion, update.Reason);
                // Spawn the staged updater (it stops this service, swaps the
                // binaries, and restarts), then stop the host so the file locks
                // release. No OS reboot — provisioning resumes on the new binary
                // when the service comes back up.
                try
                {
                    updateLauncher.Launch(new Update.UpdatePlan
                    {
                        StagingDirectory = update.StagingDirectory,
                        TargetVersion = update.TargetVersion,
                    });
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to launch the updater; leaving the current version running.");
                    // Don't stop the host — the agent keeps serving on the old
                    // binary; the next boot will retry the staged update.
                    return;
                }
                Environment.ExitCode = 0;
                lifetime.StopApplication();
                return;

            case StageRunOutcome.Failed failed:
                logger.LogError("Provisioning failed: {Reason}", failed.Reason);
                Environment.ExitCode = 1;
                // Do NOT stop the host: egs-service keeps running its other
                // responsibilities.
                return;
        }
    }

    private static void TriggerReboot()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "shutdown.exe",
                Arguments = "/r /t 5 /c \"eryph provisioning reboot\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch
        {
            // Best-effort: if shutdown.exe is unavailable the next run will
            // start fresh anyway.
        }
    }
}
