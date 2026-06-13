namespace Eryph.GuestServices.Service.Services;

// Maps a cloud-init status (cloud-init status --format json -> "status") to the
// eryph.provisioning.state value. cloud-init folds recoverable errors into
// "done"/"running" (the detail lives in extended_status), so the base status is
// enough for the single provisioning-state value.
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
