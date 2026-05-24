namespace Eryph.GuestServices.CloudConfig.Linux;

/// <summary>
/// Cloud-init <c>cc_ca_certs</c> — CA certificate installation. Linux-only;
/// the Windows certificate store has different semantics and an EGS module
/// for it is not yet implemented.
/// </summary>
[CloudInitRecord]
public sealed record CaCertsConfig
{
    /// <summary>
    /// When true, the distribution's bundled CA certificates are removed
    /// before <see cref="Trusted"/> is installed. Canonical snake-case form.
    /// </summary>
    public bool? RemoveDefaults { get; init; }

    /// <summary>
    /// Deprecated hyphenated spelling kept for cloud-init parity. Some older
    /// docs / examples use <c>remove-defaults</c>; we accept it so cross-
    /// cloud cloud-config round-trips cleanly. The YAML alias is wired up
    /// externally via <c>WithAttributeOverride</c> so the model stays
    /// YamlDotNet-free. Operators should prefer <see cref="RemoveDefaults"/>.
    /// </summary>
    public bool? RemoveDefaultsLegacy { get; init; }

    /// <summary>PEM-encoded CA certificate blobs to install.</summary>
    public IReadOnlyList<string>? Trusted { get; init; }
}
