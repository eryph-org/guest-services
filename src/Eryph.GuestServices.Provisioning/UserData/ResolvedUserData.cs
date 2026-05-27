using CloudConfigModel = Eryph.GuestServices.CloudConfig.CloudConfig;

namespace Eryph.GuestServices.Provisioning.UserData;

public sealed record ResolvedUserData
{
    public required CloudConfigModel CloudConfig { get; init; }
    public required IReadOnlyList<ScriptPayload> Scripts { get; init; }
    public required IReadOnlyList<BoothookPayload> Boothooks { get; init; }

    public static ResolvedUserData Empty(CloudConfigModel cloudConfig) => new()
    {
        CloudConfig = cloudConfig,
        Scripts = [],
        Boothooks = [],
    };

    /// <summary>
    /// Combines two resolved payloads with cloud-init precedence: user-data
    /// (<paramref name="higher"/>) wins over vendor-data (<paramref name="lower"/>)
    /// on cloud-config conflicts, and scripts / boothooks are concatenated
    /// lower-first so vendor-data entries run before user-data entries. Mirrors
    /// cloud-init treating vendor-data as a lower-priority user-data source.
    /// </summary>
    public static ResolvedUserData Combine(ResolvedUserData lower, ResolvedUserData higher) => new()
    {
        CloudConfig = Eryph.GuestServices.CloudConfig.CloudConfigMerge.Merge(
            lower.CloudConfig, higher.CloudConfig),
        Scripts = [.. lower.Scripts, .. higher.Scripts],
        Boothooks = [.. lower.Boothooks, .. higher.Boothooks],
    };
}

public sealed record ScriptPayload(ScriptKind Kind, byte[] Body, string? Filename);

public sealed record BoothookPayload(byte[] Body, string? Filename);

public enum ScriptKind
{
    ShellScript,
    PowerShell,
    Cmd,
    Other,
}
