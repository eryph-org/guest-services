namespace Eryph.GuestServices.CloudConfig.Linux;

/// <summary>
/// Cloud-init <c>cc_apt_configure</c> primary/security mirror entry. One
/// entry per arch-tagged mirror. Linux-only configuration; no-op on Windows.
/// </summary>
[CloudInitRecord]
public sealed record AptMirrorEntry
{
    /// <summary>Architectures the entry applies to (e.g. <c>amd64</c>, <c>arm64</c>, or <c>default</c>).</summary>
    public IReadOnlyList<string>? Arches { get; init; }

    /// <summary>Explicit mirror URI; mutually exclusive with <see cref="Search"/>/<see cref="SearchDns"/>.</summary>
    public string? Uri { get; init; }

    /// <summary>Ordered candidate mirror URIs cloud-init probes for reachability.</summary>
    public IReadOnlyList<string>? Search { get; init; }

    /// <summary>When true, cloud-init derives candidate mirrors from DNS.</summary>
    public bool? SearchDns { get; init; }

    /// <summary>GPG key id imported into apt's trust store for this mirror.</summary>
    public string? Keyid { get; init; }

    /// <summary>Inline GPG key block imported into apt's trust store.</summary>
    public string? Key { get; init; }

    /// <summary>When true, the entry is prepended (rather than appended) to apt's sources list.</summary>
    public bool? Prepend { get; init; }
}
