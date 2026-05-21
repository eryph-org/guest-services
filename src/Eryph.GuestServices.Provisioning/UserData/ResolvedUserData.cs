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
