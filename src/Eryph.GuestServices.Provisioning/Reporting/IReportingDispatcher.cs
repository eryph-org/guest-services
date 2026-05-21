namespace Eryph.GuestServices.Provisioning.Reporting;

public interface IReportingDispatcher
{
    Task EmitAsync(Events.ReportingEvent reportingEvent, CancellationToken cancellationToken);
}
