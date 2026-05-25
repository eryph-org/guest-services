namespace Eryph.GuestServices.CloudConfig.Linux;

/// <summary>
/// Cloud-init <c>cc_apt_configure</c> top-level <c>apt:</c> block. Mirrors
/// the documented schema; Linux-only configuration, no-op on Windows.
/// </summary>
[CloudInitRecord]
public sealed record AptConfig
{
    /// <summary>When true, cloud-init preserves the existing /etc/apt/sources.list verbatim.</summary>
    public bool? PreserveSourcesList { get; init; }

    /// <summary>Suites masked out of generated apt sources (e.g. <c>$RELEASE-security</c>).</summary>
    public IReadOnlyList<string>? DisableSuites { get; init; }

    /// <summary>Primary mirror entries; selects the URI in /etc/apt/sources.list per arch.</summary>
    public IReadOnlyList<AptMirrorEntry>? Primary { get; init; }

    /// <summary>Security mirror entries (Ubuntu-specific).</summary>
    public IReadOnlyList<AptMirrorEntry>? Security { get; init; }

    /// <summary>Verbatim /etc/apt/sources.list contents.</summary>
    public string? SourcesList { get; init; }

    /// <summary>Verbatim /etc/apt/apt.conf.d/ snippet.</summary>
    public string? Conf { get; init; }

    /// <summary>HTTP proxy URL for apt.</summary>
    public string? HttpProxy { get; init; }

    /// <summary>HTTPS proxy URL for apt.</summary>
    public string? HttpsProxy { get; init; }

    /// <summary>FTP proxy URL for apt.</summary>
    public string? FtpProxy { get; init; }

    /// <summary>Shorthand applied to all three protocols when the per-protocol forms are omitted.</summary>
    public string? Proxy { get; init; }

    /// <summary>Regex used by cloud-init to decide whether a string is an apt repo line.</summary>
    public string? AddAptRepoMatch { get; init; }

    /// <summary>Named source entries dropped under /etc/apt/sources.list.d/.</summary>
    public IReadOnlyDictionary<string, AptSourceEntry>? Sources { get; init; }
}
