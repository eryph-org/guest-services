using System.Diagnostics;
using Eryph.GuestServices.Provisioning.Reporting;
using Eryph.GuestServices.Provisioning.Stages;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Eryph.GuestServices.Provisioning.Hosting;

internal sealed class ProvisioningWorker(
    IStageRunner stageRunner,
    IHostStatusReporter reporter,
    IHostApplicationLifetime lifetime,
    ILogger<ProvisioningWorker> logger) : BackgroundService
{
    public int ExitCode { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        StageRunOutcome outcome;
        try
        {
            outcome = await stageRunner.RunAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled exception in stage runner");
            await reporter.ReportFailedAsync(ex.Message, CancellationToken.None).ConfigureAwait(false);
            SetExitCode(1);
            lifetime.StopApplication();
            return;
        }

        switch (outcome)
        {
            case StageRunOutcome.Success:
            case StageRunOutcome.NoDataSource:
                SetExitCode(0);
                lifetime.StopApplication();
                return;

            case StageRunOutcome.RebootRequested reboot:
                logger.LogInformation("Reboot requested: {Reason}", reboot.Reason);
                TriggerReboot();
                // Give shutdown.exe a brief moment to dispatch before we stop the host,
                // then exit through the normal host lifecycle so finalizers run.
                SetExitCode(0);
                try
                {
                    await Task.Delay(500, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Shutdown already in progress — fall through and stop.
                }
                lifetime.StopApplication();
                return;

            case StageRunOutcome.Failed failed:
                logger.LogError("Provisioning failed: {Reason}", failed.Reason);
                SetExitCode(1);
                lifetime.StopApplication();
                return;
        }
    }

    private void SetExitCode(int code)
    {
        ExitCode = code;
        // Surface to the process so the service manager observes the right code
        // even if a later stage of host shutdown overwrites Environment.ExitCode.
        Environment.ExitCode = code;
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
            // Best-effort: if shutdown.exe is unavailable the next run will start fresh anyway.
        }
    }
}
