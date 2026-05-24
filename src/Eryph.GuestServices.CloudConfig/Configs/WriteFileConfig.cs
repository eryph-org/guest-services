namespace Eryph.GuestServices.CloudConfig;

[CloudInitRecord]
public sealed record WriteFileConfig
{
    public string? Path { get; init; }

    public string? Content { get; init; }

    public string? Owner { get; init; }

    public string? Permissions { get; init; }

    public string? Encoding { get; init; }

    public bool? Append { get; init; }

    public bool? Defer { get; init; }
}
