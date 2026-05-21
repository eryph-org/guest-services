namespace Eryph.GuestServices.Provisioning.Reporting;

// Legacy KVP key scheme used by the now-deleted KvpHostStatusReporter — preserved
// for Agent X to port into the new KvpReportingHandler:
//   eryph.provisioning.state    : started | running | reboot_pending | completed | failed
//   eryph.provisioning.instance : <instance-id>
//   eryph.provisioning.stage    : <stage-name>
//   eryph.provisioning.reboot_reason
//   eryph.provisioning.error
//   eryph.provisioning.updated  : ISO-8601 timestamp

// Default registration. Agent X replaces with multi-handler dispatcher.
internal sealed class NullReportingDispatcher : IReportingDispatcher
{
    public Task EmitAsync(Events.ReportingEvent reportingEvent, CancellationToken cancellationToken)
        => Task.CompletedTask;
}
