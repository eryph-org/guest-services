namespace Eryph.GuestServices.Provisioning.Reporting.Events;

public abstract record ReportingEvent
{
    public required string Origin { get; init; }          // e.g. "stage:Network", "module:SetHostnameModule"
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    public sealed record ProvisioningStarted(string InstanceId) : ReportingEvent;
    public sealed record StageStarted(Stages.Stage Stage) : ReportingEvent;
    public sealed record StageFinished(Stages.Stage Stage) : ReportingEvent;
    public sealed record ModuleStarted(string ModuleName) : ReportingEvent;
    public sealed record ModuleFinished(string ModuleName, string Outcome) : ReportingEvent;
    public sealed record ModuleFailed(string ModuleName, string Reason, Exception? Exception) : ReportingEvent;
    public sealed record RebootRequested(string Reason) : ReportingEvent;
    public sealed record Progress(string Message) : ReportingEvent;
    public sealed record ProvisioningCompleted : ReportingEvent;
    public sealed record ProvisioningFailed(string Reason, Exception? Exception) : ReportingEvent;
}
