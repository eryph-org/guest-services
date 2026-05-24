namespace Eryph.GuestServices.CloudConfig;

[CloudInitRecord]
public sealed record ChpasswdConfig
{
    public bool? Expire { get; init; }

    [MergeBehavior(MergeKind.KeyedByName, KeyedMergeMethod = "MergeChpasswdEntry")]
    public IReadOnlyList<ChpasswdListEntry>? Users { get; init; }

    public string? List { get; init; }
}
