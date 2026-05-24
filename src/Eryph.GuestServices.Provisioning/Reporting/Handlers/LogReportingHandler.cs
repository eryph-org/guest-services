using Eryph.GuestServices.Provisioning.Reporting.Events;
using Microsoft.Extensions.Logging;

namespace Eryph.GuestServices.Provisioning.Reporting.Handlers;

// Always-on handler that mirrors reporting events to the standard ILogger sink.
internal sealed class LogReportingHandler(ILogger<LogReportingHandler> logger) : IReportingHandler
{
    public bool IsApplicable => true;

    public Task PublishAsync(ReportingEvent reportingEvent, CancellationToken cancellationToken)
    {
        switch (reportingEvent)
        {
            case ReportingEvent.ProvisioningStarted started:
                logger.LogInformation(
                    "[{Origin}] provisioning started for instance {InstanceId}",
                    started.Origin,
                    started.InstanceId);
                break;

            case ReportingEvent.StageStarted stageStarted:
                logger.LogInformation(
                    "[{Origin}] stage {Stage} started",
                    stageStarted.Origin,
                    stageStarted.Stage);
                break;

            case ReportingEvent.StageFinished stageFinished:
                logger.LogDebug(
                    "[{Origin}] stage {Stage} finished",
                    stageFinished.Origin,
                    stageFinished.Stage);
                break;

            case ReportingEvent.ModuleStarted moduleStarted:
                logger.LogInformation(
                    "[{Origin}] module {Module} started",
                    moduleStarted.Origin,
                    moduleStarted.ModuleName);
                break;

            case ReportingEvent.ModuleFinished moduleFinished:
                logger.LogDebug(
                    "[{Origin}] module {Module} finished: {Outcome}",
                    moduleFinished.Origin,
                    moduleFinished.ModuleName,
                    moduleFinished.Outcome);
                break;

            case ReportingEvent.ModuleFailed moduleFailed:
                logger.LogError(
                    moduleFailed.Exception,
                    "[{Origin}] module {Module} failed: {Reason}",
                    moduleFailed.Origin,
                    moduleFailed.ModuleName,
                    moduleFailed.Reason);
                break;

            case ReportingEvent.RebootRequested reboot:
                logger.LogInformation(
                    "[{Origin}] reboot requested: {Reason}",
                    reboot.Origin,
                    reboot.Reason);
                break;

            case ReportingEvent.Progress progress:
                logger.LogInformation(
                    "[{Origin}] {Message}",
                    progress.Origin,
                    progress.Message);
                break;

            case ReportingEvent.SshHostKeysReported sshHostKeys:
                foreach (var fingerprint in sshHostKeys.Fingerprints)
                {
                    logger.LogInformation(
                        "[{Origin}] ssh host key {KeyType}: {Fingerprint}",
                        sshHostKeys.Origin,
                        fingerprint.KeyType,
                        fingerprint.Fingerprint);
                }
                break;

            case ReportingEvent.ProvisioningCompleted completed:
                logger.LogInformation(
                    "[{Origin}] provisioning completed",
                    completed.Origin);
                break;

            case ReportingEvent.ProvisioningFailed failed:
                logger.LogError(
                    failed.Exception,
                    "[{Origin}] provisioning failed: {Reason}",
                    failed.Origin,
                    failed.Reason);
                break;
        }

        return Task.CompletedTask;
    }
}
