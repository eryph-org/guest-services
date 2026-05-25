namespace Eryph.GuestServices.CloudConfig.Linux;

/// <summary>
/// Cloud-init <c>cc_snap</c> top-level <c>snap:</c> block. Linux-only
/// configuration; no-op on Windows (snap has no Windows analogue).
/// </summary>
[CloudInitRecord]
public sealed record SnapConfig
{
    /// <summary>
    /// <c>snap</c> CLI invocations. Each entry is a flattened command line —
    /// same shape as <see cref="RuncmdEntry.Command"/>. Cloud-init accepts
    /// either a shell-string or an argv list; both flatten to a single string
    /// here via the runcmd-style YAML converter (when wired).
    /// </summary>
    public IReadOnlyList<string>? Commands { get; init; }

    /// <summary>
    /// snap assertions injected via <c>snap ack</c>. cloud-init also accepts
    /// a deprecated dict form keyed by assertion name; we only model the
    /// supported list shape.
    /// </summary>
    public IReadOnlyList<string>? Assertions { get; init; }
}
