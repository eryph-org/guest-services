namespace Eryph.GuestServices.Provisioning.Reporting;

public interface IReportingHandler
{
    bool IsApplicable { get; }
    Task PublishAsync(Events.ReportingEvent reportingEvent, CancellationToken cancellationToken);
}
