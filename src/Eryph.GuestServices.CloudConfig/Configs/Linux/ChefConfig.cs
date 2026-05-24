namespace Eryph.GuestServices.CloudConfig.Linux;

/// <summary>
/// Cloud-init <c>cc_chef</c> bootstrap configuration. Linux-only;
/// no-op on Windows today.
/// </summary>
[CloudInitRecord]
public sealed record ChefConfig
{
    /// <summary>Chef directories that cloud-init pre-creates (e.g. <c>/etc/chef</c>).</summary>
    public IReadOnlyList<string>? Directories { get; init; }

    /// <summary>Path or PEM body for the validation certificate.</summary>
    public string? ValidationCert { get; init; }

    /// <summary>Path for the validation key.</summary>
    public string? ValidationKey { get; init; }

    /// <summary>Validator client name.</summary>
    public string? ValidationName { get; init; }

    /// <summary>Chef server URL.</summary>
    public string? ServerUrl { get; init; }

    /// <summary>Node name; defaults to the instance hostname.</summary>
    public string? NodeName { get; init; }

    /// <summary>Chef environment (e.g. <c>_default</c>).</summary>
    public string? Environment { get; init; }

    /// <summary>chef-client log level (e.g. <c>:info</c>).</summary>
    public string? LogLevel { get; init; }

    /// <summary>Path to the chef-client log file.</summary>
    public string? LogLocation { get; init; }

    /// <summary>Path to the chef-client pid file.</summary>
    public string? PidFile { get; init; }

    /// <summary>SSL verification mode (e.g. <c>:verify_peer</c>).</summary>
    public string? SslVerifyMode { get; init; }

    /// <summary>When true, the omnibus installer runs even if chef-client is already present.</summary>
    public bool? ForceInstall { get; init; }

    /// <summary>Omnibus installer URL.</summary>
    public string? OmnibusUrl { get; init; }

    /// <summary>Omnibus installer download retry count.</summary>
    public int? OmnibusUrlRetries { get; init; }

    /// <summary>Pinned omnibus version.</summary>
    public string? OmnibusVersion { get; init; }

    /// <summary>Install type — <c>omnibus</c>, <c>packages</c>, or <c>gems</c>.</summary>
    public string? InstallType { get; init; }

    /// <summary>Initial chef-client run-list.</summary>
    public IReadOnlyList<string>? RunList { get; init; }

    /// <summary>
    /// Initial node attributes merged into the node JSON. Opaque pass-through
    /// — cloud-init treats this as an arbitrary nested mapping.
    /// </summary>
    public IReadOnlyDictionary<string, object?>? InitialAttributes { get; init; }

    /// <summary>When true, cloud-init runs <c>chef-client</c> after bootstrap.</summary>
    public bool? Exec { get; init; }

    /// <summary>Chef license acceptance flag (e.g. <c>accept</c>, <c>accept-no-persist</c>).</summary>
    public string? ChefLicense { get; init; }

    /// <summary>Encrypted data-bag secret (path or value).</summary>
    public string? EncryptedDataBagSecret { get; init; }

    /// <summary>When true, the chef-client log timestamps each line.</summary>
    public bool? ShowTime { get; init; }
}
