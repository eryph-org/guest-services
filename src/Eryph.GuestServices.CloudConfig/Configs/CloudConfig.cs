namespace Eryph.GuestServices.CloudConfig;

public sealed record CloudConfig
{
    public string? Hostname { get; init; }

    public string? Fqdn { get; init; }

    public bool? PreserveHostname { get; init; }

    public IReadOnlyList<UserConfig>? Users { get; init; }

    public IReadOnlyList<GroupConfig>? Groups { get; init; }

    public ChpasswdConfig? Chpasswd { get; init; }

    public string? Password { get; init; }

    public bool? SshPwauth { get; init; }

    public IReadOnlyList<string>? SshAuthorizedKeys { get; init; }

    public IReadOnlyList<WriteFileConfig>? WriteFiles { get; init; }

    public IReadOnlyList<RuncmdEntry>? Runcmd { get; init; }
}
