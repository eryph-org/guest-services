namespace Eryph.GuestServices.CloudConfig;

[CloudInitRecord]
public sealed record GroupConfig
{
    public string? Name { get; init; }

    public IReadOnlyList<string>? Members { get; init; }
}
