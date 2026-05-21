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
            ExitCode = 1;
            lifetime.StopApplication();
            return;
        }

        switch (outcome)
        {
            case StageRunOutcome.Success:
            case StageRunOutcome.NoDataSource:
                lifetime.StopApplication();
                return;

            case StageRunOutcome.RebootRequested reboot:
                logger.LogInformation("Reboot requested: {Reason}", reboot.Reason);
                TriggerReboot();
                Environment.Exit(0);
                return;

            case StageRunOutcome.Failed failed:
                logger.LogError("Provisioning failed: {Reason}", failed.Reason);
                ExitCode = 1;
                lifetime.StopApplication();
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
            // Best-effort: if shutdown.exe is unavailable the next run will start fresh anyway.
        }
    }
}
