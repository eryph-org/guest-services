namespace Eryph.GuestServices.CloudConfig.Linux;

/// <summary>
/// Cloud-init <c>cc_disk_setup</c> per-disk entry. The dict key is the
/// device path (e.g. <c>/dev/sdb</c>) and the value carries the partition
/// table directives. Linux-only configuration; no-op on Windows.
/// </summary>
[CloudInitRecord]
public sealed record DiskSetupEntry
{
    /// <summary>Partition table type — <c>mbr</c> or <c>gpt</c>.</summary>
    public string? TableType { get; init; }

    /// <summary>
    /// Partition layout directive. cloud-init accepts a bool (use the entire
    /// disk as one partition) or a list-of-sizes shape; modeled as
    /// <c>string?</c> here because typed-target deserialisation needs to
    /// tolerate the bool-or-list union without losing fidelity.
    /// </summary>
    public string? Layout { get; init; }

    /// <summary>When true, cloud-init overwrites any existing partition table.</summary>
    public bool? Overwrite { get; init; }
}
