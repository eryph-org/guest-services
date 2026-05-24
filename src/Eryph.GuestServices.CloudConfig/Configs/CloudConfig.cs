namespace Eryph.GuestServices.CloudConfig;

[CloudInitRoot]
[CloudInitRecord]
public sealed record CloudConfig
{
    [CloudInitField]
    public string? Hostname { get; init; }

    [CloudInitField]
    public string? Fqdn { get; init; }

    [CloudInitField]
    public bool? PreserveHostname { get; init; }

    [CloudInitField]
    [MergeBehavior(MergeKind.KeyedByName, KeyedMergeMethod = "MergeUser")]
    public IReadOnlyList<UserConfig>? Users { get; init; }

    [CloudInitField]
    [MergeBehavior(MergeKind.KeyedByName, KeyedMergeMethod = "MergeGroup")]
    public IReadOnlyList<GroupConfig>? Groups { get; init; }

    [CloudInitField]
    public ChpasswdConfig? Chpasswd { get; init; }

    [CloudInitField]
    public string? Password { get; init; }

    [CloudInitField]
    public bool? SshPwauth { get; init; }

    [CloudInitField]
    public IReadOnlyList<string>? SshAuthorizedKeys { get; init; }

    [CloudInitField]
    public IReadOnlyList<WriteFileConfig>? WriteFiles { get; init; }

    [CloudInitField]
    public IReadOnlyList<RuncmdEntry>? Runcmd { get; init; }

    [CloudInitField]
    public GrowpartConfig? Growpart { get; init; }

    [CloudInitField]
    public NtpConfig? Ntp { get; init; }

    [CloudInitField]
    public string? Timezone { get; init; }

    [CloudInitField]
    public string? Locale { get; init; }

    [CloudInitField]
    public KeyboardConfig? Keyboard { get; init; }

    [CloudInitField]
    public LicenseConfig? License { get; init; }

    // ---------------------------------------------------------------------
    // Known cloud-init top-level keys that the agent ACCEPTS but does not
    // act on. Modeled as object? so YamlDotNet round-trips arbitrary shapes
    // without us tracking every Linux module's schema. CloudConfigSerializer
    // walks these after parsing and emits one Info line per non-null entry
    // — operators get a clear "we saw your apt: block, it's a Linux concept,
    // safely ignored" signal instead of either a Warning or silent drop.
    //
    // These exist primarily so cross-cloud cloud-config YAML written for
    // Linux + Windows guests round-trips through the parser without
    // operator-visible noise on the keys that have no Windows analogue.
    // ---------------------------------------------------------------------

    /// <summary>Linux APT package source configuration. Accepted; no-op on Windows.</summary>
    [CloudInitField(Platforms = CloudInitPlatforms.Linux, Description = "Linux APT package source configuration")]
    public object? Apt { get; init; }

    /// <summary>Linux APT pipelining flag. Accepted; no-op on Windows.</summary>
    [CloudInitField(Platforms = CloudInitPlatforms.Linux, Description = "Linux APT pipelining")]
    public object? AptPipelining { get; init; }

    /// <summary>Linux package list. Future: may map to chocolatey / winget. Accepted; no-op today.</summary>
    [CloudInitField(Platforms = CloudInitPlatforms.Linux, Description = "Linux package list (no Windows package-manager binding yet)")]
    public object? Packages { get; init; }

    [CloudInitField(Platforms = CloudInitPlatforms.Linux, Description = "Linux package-manager refresh")]
    public bool? PackageUpdate { get; init; }

    [CloudInitField(Platforms = CloudInitPlatforms.Linux, Description = "Linux package-manager upgrade")]
    public bool? PackageUpgrade { get; init; }

    [CloudInitField(Platforms = CloudInitPlatforms.Linux, Description = "Linux post-upgrade reboot trigger")]
    public bool? PackageRebootIfRequired { get; init; }

    /// <summary>Linux Snap configuration. Accepted; no-op on Windows.</summary>
    [CloudInitField(Platforms = CloudInitPlatforms.Linux, Description = "Linux Snap configuration")]
    public object? Snap { get; init; }

    /// <summary>Linux YUM repos. Accepted; no-op on Windows.</summary>
    [CloudInitField(Platforms = CloudInitPlatforms.Linux, Description = "Linux YUM repositories")]
    public object? YumRepos { get; init; }

    [CloudInitField(Platforms = CloudInitPlatforms.Linux, Description = "Linux YUM repo directory")]
    public string? YumRepoDir { get; init; }

    /// <summary>Linux disk/partition setup directives. Accepted; we have <c>growpart</c> for our use case.</summary>
    [CloudInitField(Platforms = CloudInitPlatforms.Linux, Description = "Linux disk-partition setup (use 'growpart' on Windows)")]
    public object? DiskSetup { get; init; }

    /// <summary>Linux filesystem setup directives. Accepted; no-op on Windows.</summary>
    [CloudInitField(Platforms = CloudInitPlatforms.Linux, Description = "Linux filesystem-setup directives")]
    public object? FsSetup { get; init; }

    /// <summary>Linux mount points. Accepted; no-op on Windows.</summary>
    [CloudInitField(Platforms = CloudInitPlatforms.Linux, Description = "Linux mount points")]
    public object? Mounts { get; init; }

    /// <summary><c>true</c>/<c>false</c>/<c>localhost</c>/<c>template</c>. Linux /etc/hosts management. Accepted; no-op on Windows (Windows manages hosts differently).</summary>
    [CloudInitField(Platforms = CloudInitPlatforms.Linux, Description = "Linux /etc/hosts management")]
    public object? ManageEtcHosts { get; init; }

    [CloudInitField(Platforms = CloudInitPlatforms.Linux, Description = "Linux /etc/resolv.conf management")]
    public bool? ManageResolvConf { get; init; }

    /// <summary>Linux /etc/resolv.conf contents. Accepted; ApplyNetworkConfig handles DNS on Windows.</summary>
    [CloudInitField(Platforms = CloudInitPlatforms.Linux, Description = "Linux /etc/resolv.conf contents (use 'network-config' on Windows)")]
    public object? ResolvConf { get; init; }

    /// <summary>Runs on every boot, very early. Cloud-init has <c>bootcmd</c>; we don't yet — accepted as no-op.</summary>
    [CloudInitField(Platforms = CloudInitPlatforms.Linux, Description = "cloud-init bootcmd (not yet implemented on Windows)")]
    public object? Bootcmd { get; init; }

    /// <summary>
    /// Cloud-init compatible end-of-provisioning reboot/poweroff directive.
    /// Handled by <c>PowerStateModule</c> (cf. RFC 0024). NOT an
    /// acknowledged-but-no-op key — actual semantics apply.
    /// </summary>
    [CloudInitField]
    public PowerStateConfig? PowerState { get; init; }

    /// <summary>POST instance metadata to a URL at end of provisioning. Not yet implemented.</summary>
    [CloudInitField(Platforms = CloudInitPlatforms.Linux, Description = "cloud-init phone_home (not yet implemented)")]
    public object? PhoneHome { get; init; }

    /// <summary>Custom message printed at end of provisioning. Not yet implemented.</summary>
    [CloudInitField(Platforms = CloudInitPlatforms.Linux, Description = "cloud-init final_message (not yet implemented)")]
    public string? FinalMessage { get; init; }

    /// <summary>CA certificate installation directives. Not yet implemented (Windows cert store is different from Linux).</summary>
    [CloudInitField(Platforms = CloudInitPlatforms.Linux, Description = "CA certificate installation (not yet implemented; Windows cert store differs)")]
    public object? CaCerts { get; init; }

    [CloudInitField(Platforms = CloudInitPlatforms.Linux, Description = "Linux root account management")]
    public bool? DisableRoot { get; init; }

    [CloudInitField(Platforms = CloudInitPlatforms.Linux, Description = "Linux root account management")]
    public string? DisableRootOpts { get; init; }

    /// <summary>Chef bootstrap configuration. Future module. Accepted; no-op today.</summary>
    [CloudInitField(Platforms = CloudInitPlatforms.Linux, Description = "Chef bootstrap (future)")]
    public object? Chef { get; init; }

    /// <summary>Ansible pull / push configuration. Future module. Accepted; no-op today.</summary>
    [CloudInitField(Platforms = CloudInitPlatforms.Linux, Description = "Ansible bootstrap (future)")]
    public object? Ansible { get; init; }

    /// <summary>Puppet agent configuration. Future module. Accepted; no-op today.</summary>
    [CloudInitField(Platforms = CloudInitPlatforms.Linux, Description = "Puppet bootstrap (future)")]
    public object? Puppet { get; init; }

    /// <summary>Salt minion configuration. Future module. Accepted; no-op today.</summary>
    [CloudInitField(Platforms = CloudInitPlatforms.Linux, Description = "Salt minion bootstrap (future)")]
    public object? SaltMinion { get; init; }
}
