namespace Eryph.GuestServices.CloudConfig;

/// <summary>
/// Cloud-init compatible <c>power_state</c> directive. Requests a controlled
/// reboot / poweroff at the END of provisioning. Mid-stage reboot is the
/// exit-1003 "reboot and continue" mechanism — see
/// <c>scripts-per-frequency-edge-cases</c>.
/// </summary>
[CloudInitRecord]
public sealed record PowerStateConfig
{
    /// <summary>
    /// <c>reboot</c> | <c>poweroff</c> | <c>halt</c>. Defaults to
    /// <c>reboot</c> when omitted (matches the most common operator intent).
    /// <c>halt</c> has no clean Windows analogue and falls back to hibernate
    /// with a Warning log.
    /// </summary>
    public string? Mode { get; init; }

    /// <summary>
    /// When to fire. Accepts <c>now</c>, <c>+N</c> (N minutes from now),
    /// <c>HH:MM</c> (absolute time today, or tomorrow if already past),
    /// or a plain integer (seconds from now). Default <c>now</c>.
    /// </summary>
    public string? Delay { get; init; }

    /// <summary>
    /// Operator-visible message shown via <c>shutdown.exe /c</c>. Truncated
    /// to the Windows 512-character limit.
    /// </summary>
    public string? Message { get; init; }

    /// <summary>
    /// Cloud-init reuses this as the SIGTERM-&gt;SIGKILL window for
    /// long-running processes. There is no direct Windows analogue;
    /// currently accepted for cloud-init parity but not applied.
    /// </summary>
    public int? Timeout { get; init; }

    /// <summary>
    /// Cloud-init <c>power_state.condition</c> — a bool (literal
    /// <c>true</c>/<c>false</c>) or a shell command string.
    /// <list type="bullet">
    ///   <item>bool <c>true</c> (or empty / omitted): proceed.</item>
    ///   <item>bool <c>false</c>: skip.</item>
    ///   <item>string: run as a shell command; exit code 0 proceeds, anything else skips.</item>
    /// </list>
    /// Modeled as <see cref="BoolOrString"/> so PyYAML's quoting
    /// distinction survives — plain <c>true</c> is bool, quoted
    /// <c>"true"</c> is a shell command (Linux's <c>true</c> exits 0).
    /// </summary>
    public BoolOrString Condition { get; init; }
}
