namespace Eryph.GuestServices.CloudConfig.Linux;

/// <summary>
/// Cloud-init <c>cc_apt_configure</c> <c>sources</c> map entry — one .list
/// file dropped under <c>/etc/apt/sources.list.d/</c>. Linux-only
/// configuration; no-op on Windows.
/// </summary>
[CloudInitRecord]
public sealed record AptSourceEntry
{
    /// <summary>The verbatim <c>deb ...</c> line (or <c>deb-src ...</c>).</summary>
    public string? Source { get; init; }

    /// <summary>GPG key id imported into apt's trust store.</summary>
    public string? Keyid { get; init; }

    /// <summary>GPG key server (defaults to <c>keyserver.ubuntu.com</c>).</summary>
    public string? Keyserver { get; init; }

    /// <summary>Inline GPG key block.</summary>
    public string? Key { get; init; }

    /// <summary>Filename under <c>/etc/apt/sources.list.d/</c>; defaults to the dict key.</summary>
    public string? Filename { get; init; }

    /// <summary>When true, the entry is appended to the existing file instead of overwriting it.</summary>
    public bool? Append { get; init; }
}
