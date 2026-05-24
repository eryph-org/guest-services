namespace Eryph.GuestServices.CloudConfig.Linux;

/// <summary>
/// Cloud-init <c>cc_yum_add_repo</c> per-repo entry. Mirrors the
/// most-commonly-set yum.conf keys; cloud-init itself only validates that
/// each value is a scalar (string / bool / int) and passes the dict through
/// to <c>/etc/yum.repos.d/&lt;name&gt;.repo</c>. Linux-only configuration;
/// no-op on Windows.
/// </summary>
[CloudInitRecord]
public sealed record YumRepoConfig
{
    /// <summary>Repository URL.</summary>
    public string? Baseurl { get; init; }

    /// <summary>Human-readable repository name.</summary>
    public string? Name { get; init; }

    /// <summary>Whether the repository is enabled.</summary>
    public bool? Enabled { get; init; }

    /// <summary>GPG public key URL.</summary>
    public string? Gpgkey { get; init; }

    /// <summary>When true, yum verifies package signatures against the gpgkey.</summary>
    public bool? Gpgcheck { get; init; }

    /// <summary>Cache expiry interval (e.g. <c>86400</c>).</summary>
    public string? MetadataExpire { get; init; }

    /// <summary>When true, yum continues if the repo is unreachable.</summary>
    public bool? SkipIfUnavailable { get; init; }

    /// <summary>Repository priority (lower = higher priority).</summary>
    public int? Priority { get; init; }

    /// <summary>Mirror list URL (mutually exclusive with <see cref="Baseurl"/>).</summary>
    public string? Mirrorlist { get; init; }
}
