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

    public static ProvisioningSettings LoadOrDefault()
    {
        foreach (var candidate in CandidatePaths())
        {
            if (!File.Exists(candidate))
                continue;

            try
            {
                var json = File.ReadAllText(candidate);
                return JsonSerializer.Deserialize(json, SettingsSerializerContext.Default.ProvisioningSettings)
                       ?? new ProvisioningSettings();
            }
            catch (Exception)
            {
                // Ignore malformed settings files; defaults are safer than failing the run.
                // The host operator can inspect the file and fix it; we log via DI logger
                // once the host is up, but at construction time we have no logger.
                return new ProvisioningSettings();
            }
        }
        return new ProvisioningSettings();
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
}

public sealed class DataSourceSettings
{
    /// <summary>Per-datasource cap on total time spent in <c>WaitForReady</c> retries.</summary>
    public int ProbeTimeoutMinutes { get; init; } = 10;
}

public sealed class ScriptSettings
{
    /// <summary>Directory where user-data scripts are staged before execution.</summary>
    public string PerInstanceDirectory { get; init; } =
        @"%ProgramData%\eryph\provisioning\scripts\per-instance";

    /// <summary>Per-script execution timeout. Not enforced in v1; reserved for v2.</summary>
    public int ScriptTimeoutMinutes { get; init; } = 60;
}

[JsonSerializable(typeof(ProvisioningSettings))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
internal sealed partial class SettingsSerializerContext : JsonSerializerContext;
