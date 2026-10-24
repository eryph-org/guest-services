namespace Eryph.GuestServices.Provisioning.Windows;

/// <summary>
/// Outcome of a single <c>dsc.exe config &lt;verb&gt; --file &lt;document&gt;</c>
/// run. <see cref="RebootRequested"/> is aggregated across the document's
/// per-resource results; the module maps it to <c>ModuleOutcome.Reboot</c>
/// (RFC 0025). <see cref="JsonOutput"/> is <c>dsc.exe</c>'s raw stdout JSON and
/// <see cref="StdErr"/> its stderr — both kept so the module can surface
/// diagnostics when a run fails.
/// </summary>
public sealed record DscApplyResult
{
    public int ExitCode { get; init; }

    public bool RebootRequested { get; init; }

    public string JsonOutput { get; init; } = "";

    public string StdErr { get; init; } = "";
}
