namespace Eryph.GuestServices.Provisioning.Reporting;

public interface IHostStatusReporter
{
    Task ReportStartedAsync(string instanceId, CancellationToken cancellationToken);

    Task ReportStageCompletedAsync(string stage, CancellationToken cancellationToken);

    Task ReportRebootPendingAsync(string reason, CancellationToken cancellationToken);

    Task ReportCompletedAsync(CancellationToken cancellationToken);

    Task ReportFailedAsync(string error, CancellationToken cancellationToken);
}
