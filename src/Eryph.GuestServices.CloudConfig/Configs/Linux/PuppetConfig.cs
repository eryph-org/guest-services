namespace Eryph.GuestServices.CloudConfig.Linux;

/// <summary>
/// Cloud-init <c>cc_puppet</c> agent configuration. Linux-only;
/// no-op on Windows today.
/// </summary>
[CloudInitRecord]
public sealed record PuppetConfig
{
    /// <summary>When true, cloud-init installs the puppet agent if absent.</summary>
    public bool? Install { get; init; }

    /// <summary>Pinned puppet version.</summary>
    public string? Version { get; init; }

    /// <summary>Install method — <c>aio</c> or <c>packages</c>.</summary>
    public string? InstallType { get; init; }

    /// <summary>Puppet collection (e.g. <c>puppet7</c>).</summary>
    public string? Collection { get; init; }

    /// <summary>AIO installer URL.</summary>
    public string? AioInstallUrl { get; init; }

    /// <summary>When true, the AIO installer script is removed after install.</summary>
    public bool? Cleanup { get; init; }

    /// <summary>Package name to install (alternative to AIO).</summary>
    public string? PackageName { get; init; }

    /// <summary>When true, cloud-init runs <c>puppet agent</c> after install.</summary>
    public bool? Exec { get; init; }

    /// <summary>Extra arguments passed to <c>puppet agent</c>.</summary>
    public IReadOnlyList<string>? ExecArgs { get; init; }

    /// <summary>When true, cloud-init starts the puppet agent service.</summary>
    public bool? StartService { get; init; }

    /// <summary>
    /// puppet.conf overrides, keyed by section (e.g. <c>main</c>, <c>agent</c>);
    /// per-section value is another <c>key → value</c> map.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? Conf { get; init; }

    /// <summary>
    /// CSR attributes injected into <c>csr_attributes.yaml</c>, keyed by
    /// section (e.g. <c>custom_attributes</c>, <c>extension_requests</c>).
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? CsrAttributes { get; init; }
}
