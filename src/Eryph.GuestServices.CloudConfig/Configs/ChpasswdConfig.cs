namespace Eryph.GuestServices.CloudConfig;

public sealed record ChpasswdConfig
{
    public bool? Expire { get; init; }

    public IReadOnlyList<ChpasswdListEntry>? Users { get; init; }

    public string? List { get; init; }
}
