namespace Eryph.GuestServices.Provisioning.Windows;

/// <summary>
/// Resolved licensing state passed to <see cref="IWindowsOs.ApplyLicenseAsync"/>.
/// The module computes this from the cloud-config (auto-detecting AVMA / KMS
/// keys against the OS edition) — by the time it reaches the OS layer all
/// indirection (set_avma, set_kms) is gone, leaving only "what to apply".
/// </summary>
public sealed record LicenseSpec
{
    public string? ProductKey { get; init; }

    public string? KmsHost { get; init; }

    /// <summary>
    /// When true, clears the configured KMS host (<c>slmgr /ckms</c>) so
    /// DNS SRV auto-discovery takes over. Only honoured when
    /// <see cref="KmsHost"/> is null — supplying both would be ambiguous.
    /// </summary>
    public bool ClearKmsHost { get; init; }

    /// <summary>
    /// When true, run <c>slmgr /ato</c> after applying the key and KMS host.
    /// </summary>
    public bool Activate { get; init; }
}

public sealed record RearmResult
{
    /// <summary>
    /// Microsoft documents that <c>slmgr /rearm</c> requires a reboot for
    /// the extension to take effect. Always true after a successful rearm.
    /// </summary>
    public bool RebootRequired { get; init; }
}

/// <summary>
/// Identifies the volume-activation key set to resolve against the guest's
/// OS edition. Mirrors the two distinct tables cloudbase-init carries.
/// </summary>
public enum VolumeActivationKeyType
{
    Kms = 0,
    Avma = 1,
}
