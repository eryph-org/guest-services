namespace Eryph.GuestServices.Service.Services;

// Maps cloud-init's `status` field (cloud-init status --format json) to the
// eryph.provisioning.state value. cloud-init's translate_status() keeps the
// `status` field to the simplified set — not started/running/done/disabled, or
// "error" — and NEVER puts "degraded" there: a degraded run reports
// status="done"/"running" and exposes the degradation only via `extended_status`
// ("degraded done"). So reading `status` alone is correct and complete for the
// single provisioning-state value; egs deliberately does not read extended_status.
internal static class CloudInitStateMapper
{
    public const string Running = "running";
    public const string Completed = "completed";
    public const string Failed = "failed";

    // Returns null when there is nothing to report yet (cloud-init "not run" or
    // an unrecognised value) so the watcher keeps polling without writing.
    public static string? Map(string? cloudInitStatus) => cloudInitStatus switch
    {
        "done" => Completed,
        "error" => Failed,
        "running" => Running,
        "disabled" => Completed,
        _ => null,
    };

    public static bool IsTerminal(string? cloudInitStatus) =>
        cloudInitStatus is "done" or "error" or "disabled";
}
