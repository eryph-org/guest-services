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

    /// <summary>
    /// A single user-supplied command (a <c>runcmd</c> entry or a user script)
    /// exited non-zero while the module continues with the remaining items
    /// (cloud-init parity — a failing script does not abort the run). Unlike
    /// <see cref="ReportingEvent.ModuleFailed"/> this does NOT flip the overall provisioning
    /// state to failed; it surfaces the failed item, its exit code and an output
    /// tail as a FAIL event in the cloud-init stream so the host-rendered
    /// provisioning log shows <em>why</em> without an operator entering the VM.
    /// </summary>
    public sealed record UserCodeFailed(string Module, string Item, int ExitCode, string OutputTail) : ReportingEvent;

    /// <summary>
    /// Carries the ssh host-key fingerprints produced by the SshModule so an
    /// operator console can display them (RFC 0018). Gated by the cloud-config
    /// <c>ssh.emit_keys_to_console</c> flag at the producing site.
    /// </summary>
    public sealed record SshHostKeysReported(
        IReadOnlyList<Windows.SshHostKeyFingerprint> Fingerprints) : ReportingEvent;
    public sealed record ProvisioningCompleted : ReportingEvent;
    public sealed record ProvisioningFailed(string Reason, Exception? Exception) : ReportingEvent;
}
