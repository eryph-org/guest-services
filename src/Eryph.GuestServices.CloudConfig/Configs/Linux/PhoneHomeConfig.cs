namespace Eryph.GuestServices.CloudConfig.Linux;

/// <summary>
/// Cloud-init <c>cc_phone_home</c> — POST selected instance metadata to a
/// URL at the end of provisioning. Linux-only today; no Windows analogue.
/// </summary>
[CloudInitRecord]
public sealed record PhoneHomeConfig
{
    /// <summary>Endpoint URL.</summary>
    public string? Url { get; init; }

    /// <summary>
    /// Fields to POST. Accepts the literal scalar <c>all</c> or a list of
    /// keys (e.g. <c>pub_key_dsa</c>, <c>pub_key_rsa</c>, <c>fqdn</c>). The
    /// scalar form is promoted to a single-element list via a YAML converter.
    /// </summary>
    public IReadOnlyList<string>? Post { get; init; }

    /// <summary>Number of HTTP retries on failure.</summary>
    public int? Tries { get; init; }
}
