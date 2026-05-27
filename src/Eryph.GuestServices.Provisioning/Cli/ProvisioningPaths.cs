using Eryph.GuestServices.Provisioning.Configuration;

namespace Eryph.GuestServices.Provisioning.Cli;

/// <summary>
/// Resolves the well-known paths the CLI needs to inspect or clean up.
/// Centralised so reset / collect-logs / status all agree on where to look.
/// Tests can override the root by setting <see cref="RootOverride"/>.
/// </summary>
internal static class ProvisioningPaths
{
    // Test-only hook. Production code leaves this null and uses
    // SpecialFolder.CommonApplicationData (i.e. %ProgramData% on Windows).
    internal static string? RootOverride { get; set; }

    public static string Root =>
        RootOverride
        ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "eryph",
            "provisioning");

    public static string StateFile => Path.Combine(Root, "state.json");

    /// <summary>
    /// File name of the local datasource cache (cloud-init <c>obj.pkl</c>
    /// analogue). Shared so <see cref="State.FileDataSourceCache"/> and the reset
    /// path can't drift.
    /// </summary>
    public const string DataSourceCacheFileName = "datasource.json";

    /// <summary>
    /// Local cache of the located datasource. Cleared on reset so the next run
    /// re-crawls the datasource.
    /// </summary>
    public static string DataSourceCacheFile => Path.Combine(Root, DataSourceCacheFileName);

    public static string LogsDirectory => Path.Combine(Root, "logs");

    /// <summary>
    /// Per-instance scope root. Mirrors cloud-init's
    /// <c>/var/lib/cloud/instance</c> — one directory per instance-id,
    /// containing the per-instance semaphore subdirectory plus any other
    /// instance-scoped artefacts we add later.
    /// </summary>
    public static string InstanceRoot => Path.Combine(Root, "instance");

    /// <summary>
    /// Global semaphore directory hosting per-boot and per-once markers.
    /// Mirrors cloud-init's <c>/var/lib/cloud/sem</c>.
    /// </summary>
    public static string GlobalSemaphoreDir => Path.Combine(Root, "sem");

    /// <summary>
    /// Marker file used by <c>BootSessionDetector</c> to remember the boot
    /// id of the previous run. Deleted on full reset so the next agent run
    /// treats itself as a new boot.
    /// </summary>
    public static string LastSeenBootFile => Path.Combine(Root, "last-seen-boot.json");

    public static string ScriptsDirectory(ProvisioningSettings settings) =>
        Environment.ExpandEnvironmentVariables(settings.Scripts.PerInstanceDirectory);
}
