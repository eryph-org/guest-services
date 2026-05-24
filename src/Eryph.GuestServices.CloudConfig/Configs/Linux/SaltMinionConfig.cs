namespace Eryph.GuestServices.CloudConfig.Linux;

/// <summary>
/// Cloud-init <c>cc_salt_minion</c> bootstrap configuration. Linux-only;
/// no-op on Windows today.
/// </summary>
[CloudInitRecord]
public sealed record SaltMinionConfig
{
    /// <summary>Package name to install (defaults to <c>salt-minion</c>).</summary>
    public string? PkgName { get; init; }

    /// <summary>systemd unit name to enable (defaults to <c>salt-minion</c>).</summary>
    public string? ServiceName { get; init; }

    /// <summary>Directory the minion config file is written to.</summary>
    public string? ConfigDir { get; init; }

    /// <summary>
    /// Verbatim minion config payload merged into <c>/etc/salt/minion</c>.
    /// Opaque pass-through — cloud-init treats this as arbitrary YAML.
    /// </summary>
    public IReadOnlyDictionary<string, object?>? Conf { get; init; }

    /// <summary>
    /// Verbatim grains payload merged into <c>/etc/salt/grains</c>.
    /// Opaque pass-through.
    /// </summary>
    public IReadOnlyDictionary<string, object?>? Grains { get; init; }

    /// <summary>Minion public key (PEM).</summary>
    public string? PublicKey { get; init; }

    /// <summary>Minion private key (PEM).</summary>
    public string? PrivateKey { get; init; }
}
