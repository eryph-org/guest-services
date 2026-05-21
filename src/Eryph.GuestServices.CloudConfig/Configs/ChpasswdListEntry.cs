namespace Eryph.GuestServices.CloudConfig;

public sealed record ChpasswdListEntry
{
    public string? Name { get; init; }

    public string? Password { get; init; }

    public string? Type { get; init; }
}
