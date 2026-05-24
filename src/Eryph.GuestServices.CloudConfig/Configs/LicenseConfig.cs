namespace Eryph.GuestServices.CloudConfig;

/// <summary>
/// Windows licensing directive. There is no cloud-init module for this —
/// it is Windows-specific (cloudbase-init's <c>WindowsLicensingPlugin</c>).
/// The module surface is intentionally minimal: a product key, an optional
/// KMS host, and an "activate now" toggle. Heavier flows (AVMA detection,
/// automatic KMS key lookup per OS edition) belong in fodder/scripts.
/// </summary>
[CloudInitRecord]
public sealed record LicenseConfig
{
    /// <summary>
    /// Product key in the canonical <c>XXXXX-XXXXX-XXXXX-XXXXX-XXXXX</c>
    /// form. Applied via <c>slmgr.vbs /ipk</c>.
    /// </summary>
    public string? ProductKey { get; init; }

    /// <summary>
    /// KMS host as <c>hostname</c> or <c>hostname:port</c>. Applied via
    /// <c>slmgr.vbs /skms</c>.
    /// </summary>
    public string? KmsHost { get; init; }

    /// <summary>
    /// When true, run <c>slmgr.vbs /ato</c> after applying the key / KMS
    /// host. Off by default — KMS clients normally activate themselves on
    /// first network connectivity.
    /// </summary>
    public bool? Activate { get; init; }

    /// <summary>
    /// When true, the agent looks up the AVMA key for the guest's OS
    /// edition (Server 2012R2+) and installs it. Mirrors cloudbase-init's
    /// <c>set_avma_product_key</c>. Ignored if <see cref="ProductKey"/> is
    /// set — an explicit key always wins.
    /// </summary>
    public bool? SetAvma { get; init; }

    /// <summary>
    /// When true, the agent looks up the generic KMS-client key for the
    /// guest's edition and installs it. Mirrors cloudbase-init's
    /// <c>set_kms_product_key</c>. <see cref="SetAvma"/> takes precedence
    /// when both are set. Ignored if <see cref="ProductKey"/> is set.
    /// </summary>
    public bool? SetKms { get; init; }

    /// <summary>
    /// When true, runs <c>slmgr.vbs /rearm</c> to extend an evaluation
    /// period. Rearm requires reboot to take effect; the module surfaces
    /// that as a RebootRequested outcome.
    /// </summary>
    public bool? Rearm { get; init; }

    /// <summary>
    /// By default the module skips itself when the active datasource is
    /// Azure — Windows on Azure activates automatically against
    /// <c>kms.core.windows.net</c> and our slmgr calls would only add
    /// noise. Set this to true to force the licensing module to run even
    /// on Azure (e.g. when applying a non-Azure KMS host).
    /// </summary>
    public bool? Force { get; init; }
}
