namespace Eryph.GuestServices.CloudConfig.Linux;

/// <summary>
/// Cloud-init <c>cc_resolv_conf</c> — /etc/resolv.conf contents. Linux-only
/// configuration; on Windows DNS is configured via the network-config layer.
/// </summary>
[CloudInitRecord]
public sealed record ResolvConfConfig
{
    /// <summary>Nameserver IPs.</summary>
    public IReadOnlyList<string>? Nameservers { get; init; }

    /// <summary>DNS search domains.</summary>
    public IReadOnlyList<string>? Searchdomains { get; init; }

    /// <summary>Single-label resolution domain.</summary>
    public string? Domain { get; init; }

    /// <summary>Sort list applied to glibc resolution.</summary>
    public IReadOnlyList<string>? Sortlist { get; init; }

    /// <summary>
    /// Resolver options (e.g. <c>rotate</c>, <c>timeout: 2</c>). Cloud-init
    /// accepts arbitrary scalar values; modeled as <c>string?</c> values for
    /// simplicity — YamlDotNet stringifies bool/int through the dict path.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Options { get; init; }
}
