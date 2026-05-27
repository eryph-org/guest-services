using System.Text.Json;
using System.Text.Json.Serialization;

namespace Eryph.GuestServices.Provisioning.Configuration;

/// <summary>
/// Tunables read from <c>egs-provisioning.json</c> next to the binary, or
/// from <c>%ProgramData%\eryph\provisioning\settings.json</c>. Falls back
/// to defaults when no file is present.
/// </summary>
public sealed class ProvisioningSettings
{
    public UserDataSettings UserData { get; init; } = new();

    public DataSourceSettings DataSources { get; init; } = new();

    public ScriptSettings Scripts { get; init; } = new();

    public RebootSettings Reboot { get; init; } = new();

    /// <summary>
    /// The image-baked default administrator concept. Layer 3 of
    /// <see cref="Modules.IDefaultUserResolver"/>: the account that top-level
    /// shorthands (<c>ssh_authorized_keys</c>, <c>password</c>,
    /// <c>chpasswd</c>) target when the user-data declares no admin user.
    /// Mirrors cloud-init's <c>system_info.default_user</c>.
    /// </summary>
    public DefaultUserSettings DefaultUser { get; init; } = new();

    /// <summary>
    /// Per-stage allowlist / denylist of modules. Keys are stage names
    /// (<c>"Local"</c>, <c>"Network"</c>, <c>"Config"</c>, <c>"Final"</c>;
    /// case-insensitive). When a stage is absent, all discovered modules
    /// for that stage run. See <see cref="StageSettings"/> for matching
    /// semantics. RFC 0009 — module-list split.
    /// </summary>
    public Dictionary<string, StageSettings>? Stages { get; init; }

    public static ProvisioningSettings LoadOrDefault()
    {
        foreach (var candidate in CandidatePaths())
        {
            if (!File.Exists(candidate))
                continue;

            return LoadFromFileOrDefault(candidate);
        }
        return new ProvisioningSettings();
    }

    /// <summary>
    /// Loads and normalizes settings from a single file. A partial file (e.g.
    /// one that pins only <c>dataSources.dataSourceList</c>) deserializes with
    /// its absent sibling sections left <c>null</c>: System.Text.Json source
    /// generation does NOT apply the C# property initializers
    /// (<c>UserData { get; init; } = new();</c>) for properties missing from the
    /// JSON. A consumer that dereferences such a section (e.g. <c>UrlHelper</c>
    /// reading <c>settings.UserData.FetchMaxAttempts</c>) would then throw
    /// <see cref="NullReferenceException"/> — which previously surfaced as a
    /// container <c>Verify()</c> failure that crash-looped egs-service on boot.
    /// We rebuild the instance here so every section is guaranteed non-null,
    /// regardless of which keys the file supplied.
    /// </summary>
    internal static ProvisioningSettings LoadFromFileOrDefault(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            var loaded = JsonSerializer.Deserialize(
                json, SettingsSerializerContext.Default.ProvisioningSettings);
            if (loaded is null)
                return new ProvisioningSettings();

            return new ProvisioningSettings
            {
                UserData = loaded.UserData ?? new UserDataSettings(),
                DataSources = loaded.DataSources ?? new DataSourceSettings(),
                Scripts = loaded.Scripts ?? new ScriptSettings(),
                Reboot = loaded.Reboot ?? new RebootSettings(),
                DefaultUser = loaded.DefaultUser ?? new DefaultUserSettings(),
                Stages = loaded.Stages,
            };
        }
        catch (Exception)
        {
            // Ignore malformed settings files; defaults are safer than failing the run.
            // The host operator can inspect the file and fix it; we log via DI logger
            // once the host is up, but at construction time we have no logger.
            return new ProvisioningSettings();
        }
    }

    private static IEnumerable<string> CandidatePaths()
    {
        yield return Path.Combine(AppContext.BaseDirectory, "egs-provisioning.json");
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (!string.IsNullOrEmpty(programData))
            yield return Path.Combine(programData, "eryph", "provisioning", "settings.json");
    }
}

public sealed class UserDataSettings
{
    /// <summary>Maximum nesting depth for multipart / #include recursion.</summary>
    public int MaxRecursionDepth { get; init; } = 10;

    /// <summary>Per-attempt timeout when fetching #include URLs.</summary>
    public int FetchTimeoutSeconds { get; init; } = 30;

    /// <summary>Total attempts (initial + retries) when fetching #include URLs.</summary>
    public int FetchMaxAttempts { get; init; } = 4;

    /// <summary>Initial backoff between #include retries. Doubles up to a 4s cap.</summary>
    public int FetchInitialBackoffSeconds { get; init; } = 1;

    /// <summary>
    /// Maximum size (in bytes) of a single #include response. A server that
    /// reports a larger <c>Content-Length</c>, or streams more bytes than this
    /// (lying about / omitting the header), aborts the fetch. Guards against a
    /// runaway or hostile URL exhausting memory. Default 10 MiB.
    /// </summary>
    public long FetchMaxBytes { get; init; } = 10L * 1024 * 1024;
}

public sealed class DataSourceSettings
{
    /// <summary>
    /// Total wall-clock budget (in minutes) <see cref="DataSources.DataSourceLocator"/>
    /// will spend across all probes — including <c>WaitForReady</c> backoffs — before
    /// giving up and returning <c>NoDataSource</c>. Mirrors cloud-init's per-datasource
    /// <c>wait_for_metadata_service</c> deadline but is global (one timer for the entire
    /// LocateAsync call), since we round-robin probes between sources rather than
    /// blocking on a single slow one. Default 15 minutes — covers Azure PA worst-case
    /// (large image / slow disk pushes the oobeSystem chain past 10 minutes), while
    /// still being short enough that a hung probe surfaces as a build failure within
    /// the typical CI cycle.
    /// </summary>
    public int ReadinessTimeoutMinutes { get; init; } = 15;

    /// <summary>
    /// Minimum backoff (seconds) between consecutive <c>WaitForReady</c> probes of the
    /// same source. The datasource's own <see cref="DataSources.DataSourceProbeResult.WaitForReady.Backoff"/>
    /// is treated as a hint; the locator clamps it to [<see cref="MinBackoffSeconds"/>,
    /// <see cref="MaxBackoffSeconds"/>] and doubles after each retry. Default 1s.
    /// </summary>
    public int MinBackoffSeconds { get; init; } = 1;

    /// <summary>
    /// Maximum backoff (seconds) between consecutive <c>WaitForReady</c> probes of the
    /// same source. Caps the exponential growth so we keep checking at least once a
    /// minute even on a long deadline. Default 60s.
    /// </summary>
    public int MaxBackoffSeconds { get; init; } = 60;

    /// <summary>
    /// Ordered list of datasource names to probe (e.g.
    /// <c>["NoCloud","ConfigDrive","Azure"]</c>), mirroring cloud-init's
    /// <c>datasource_list</c>. When null/empty, all registered sources are probed in
    /// <see cref="DataSources.IDataSource.Priority"/> order (default). When set, only the
    /// named sources are probed, <b>in the listed order</b>; names not matching a
    /// registered source are logged at Warning and ignored. Matching is case-insensitive
    /// on <see cref="DataSources.IDataSource.Name"/>.
    /// </summary>
    public List<string>? DataSourceList { get; init; }
}

/// <summary>
/// Operator-controlled allowlist / denylist of modules for a single stage.
/// Mirrors cloud-init's <c>cloud_init_modules</c> / <c>cloud_config_modules</c>
/// / <c>cloud_final_modules</c> lists.
///
/// Module-name matching is case-insensitive and tolerates the <c>Module</c>
/// suffix (so both <c>"SetHostnameModule"</c> and <c>"SetHostname"</c>
/// match the <see cref="Modules.SetHostnameModule"/> type).
///
/// Resolution order when both lists are set:
/// 1. Start with all discovered modules in the stage.
/// 2. If <see cref="EnabledModules"/> is non-null, narrow to that set.
/// 3. If <see cref="DisabledModules"/> is non-null, remove those.
///
/// Unknown names are logged at Warning but do not fail the run — typo in a
/// settings file should not crash provisioning.
/// </summary>
public sealed class StageSettings
{
    /// <summary>
    /// When set, only modules whose short class name (case-insensitive,
    /// optional <c>Module</c> suffix) appears here run in this stage.
    /// When null, all discovered modules are considered for this stage.
    /// </summary>
    public List<string>? EnabledModules { get; init; }

    /// <summary>
    /// When set, modules whose short class name matches an entry are
    /// removed from the stage's run list (applied after
    /// <see cref="EnabledModules"/> if both are set).
    /// </summary>
    public List<string>? DisabledModules { get; init; }
}

public sealed class ScriptSettings
{
    /// <summary>Directory where user-data scripts are staged before execution.</summary>
    public string PerInstanceDirectory { get; init; } =
        @"%ProgramData%\eryph\provisioning\scripts\per-instance";

    /// <summary>Per-script execution timeout. Not enforced in v1; reserved for v2.</summary>
    public int ScriptTimeoutMinutes { get; init; } = 60;
}

/// <summary>
/// Loop-safety caps for the cloudbase-init "reboot-and-continue" (exit 1003)
/// convention. Two independent guards (see docs/bugs/0001 "loop-safety"):
/// <see cref="MaxPerModule"/> bounds how often the StageRunner re-enters the
/// same module, while <see cref="MaxPerScript"/> bounds reboots for a single
/// (ordinal, body-hash) script inside ScriptsUser. The per-script cap is the
/// tighter inner guard; the per-module cap is the outer backstop. Defaults
/// match the historical hard-coded values, so behaviour is unchanged unless
/// configured.
/// </summary>
public sealed class RebootSettings
{
    /// <summary>Max times a single module may return RebootRequested before the run fails.</summary>
    public int MaxPerModule { get; init; } = 3;

    /// <summary>Max reboots a single (ordinal, body-hash) script may request before it fails.</summary>
    public int MaxPerScript { get; init; } = 2;
}

/// <summary>
/// The image-baked default administrator. The <c>Name</c> is the cloud-init
/// <c>system_info.default_user.name</c> analogue — the account top-level
/// credential shorthands target when the user-data <c>users:</c> block does
/// not declare an admin. <c>Groups</c> and <c>CreateIfMissing</c> let an image
/// pre-declare how that account is provisioned when it has to be created.
/// </summary>
public sealed record DefaultUserSettings
{
    /// <summary>
    /// The image-baked default admin name (cloud-init
    /// <c>system_info.default_user.name</c>). When null the resolver falls
    /// through to its <c>"Administrator"</c> fallback.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// Default groups for the default user. Null is treated as
    /// <c>["Administrators"]</c> by consumers.
    /// </summary>
    public IReadOnlyList<string>? Groups { get; init; }

    /// <summary>
    /// When true, the default user is auto-created (and the top-level
    /// credentials applied to it) if no <c>users:</c> entry declares one.
    /// </summary>
    public bool CreateIfMissing { get; init; }
}

[JsonSerializable(typeof(ProvisioningSettings))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
internal sealed partial class SettingsSerializerContext : JsonSerializerContext;
