namespace Eryph.GuestServices.CloudConfig.Linux;

/// <summary>
/// Cloud-init <c>cc_disk_setup</c> <c>fs_setup</c> entry — describes how to
/// format a partition. Linux-only configuration; no-op on Windows.
/// </summary>
[CloudInitRecord]
public sealed record FsSetupEntry
{
    /// <summary>Filesystem label (passed via <c>mkfs.* -L</c>).</summary>
    public string? Label { get; init; }

    /// <summary>Filesystem type (e.g. <c>ext4</c>, <c>xfs</c>, <c>swap</c>).</summary>
    public string? Filesystem { get; init; }

    /// <summary>Target device (e.g. <c>/dev/sdb</c>).</summary>
    public string? Device { get; init; }

    /// <summary>
    /// Partition selector. Cloud-init accepts <c>"auto"</c>, <c>"any"</c>,
    /// <c>"none"</c>, or a partition number — modeled as <c>string?</c> to
    /// cover all forms with one type.
    /// </summary>
    public string? Partition { get; init; }

    /// <summary>When true, cloud-init overwrites any existing filesystem.</summary>
    public bool? Overwrite { get; init; }

    /// <summary>Existing filesystem types that should be reformatted.</summary>
    public string? ReplaceFs { get; init; }

    /// <summary>Extra options passed to mkfs (e.g. <c>-E lazy_itable_init=0</c>).</summary>
    public IReadOnlyList<string>? ExtraOpts { get; init; }
}
