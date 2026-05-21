namespace Eryph.GuestServices.CloudConfig;

public sealed record UserConfig
{
    public string? Name { get; init; }

    public string? Passwd { get; init; }

    public bool? LockPasswd { get; init; }

    public IReadOnlyList<string>? Groups { get; init; }

    public IReadOnlyList<string>? SshAuthorizedKeys { get; init; }

    public bool? Inactive { get; init; }

    public string? Shell { get; init; }

    public string? HomeDir { get; init; }

    public string? PrimaryGroup { get; init; }

    public string? Sudo { get; init; }

    public bool? System { get; init; }
}
