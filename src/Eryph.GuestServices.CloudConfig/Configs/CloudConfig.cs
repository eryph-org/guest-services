namespace Eryph.GuestServices.CloudConfig;

public sealed record CloudConfig
{
    public string? Hostname { get; init; }

    public string? Fqdn { get; init; }

    public bool? PreserveHostname { get; init; }

    public IReadOnlyList<UserConfig>? Users { get; init; }

    public IReadOnlyList<GroupConfig>? Groups { get; init; }

    public ChpasswdConfig? Chpasswd { get; init; }

    public string? Password { get; init; }

    public bool? SshPwauth { get; init; }

    public IReadOnlyList<string>? SshAuthorizedKeys { get; init; }

    public IReadOnlyList<WriteFileConfig>? WriteFiles { get; init; }

    public IReadOnlyList<RuncmdEntry>? Runcmd { get; init; }

    public GrowpartConfig? Growpart { get; init; }

    public NtpConfig? Ntp { get; init; }

    public string? Timezone { get; init; }

    public string? Locale { get; init; }

    public KeyboardConfig? Keyboard { get; init; }

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
    public object? Apt { get; init; }

    /// <summary>Linux APT pipelining flag. Accepted; no-op on Windows.</summary>
    public object? AptPipelining { get; init; }

    /// <summary>Linux package list. Future: may map to chocolatey / winget. Accepted; no-op today.</summary>
    public object? Packages { get; init; }

    public bool? PackageUpdate { get; init; }

    public bool? PackageUpgrade { get; init; }

    public bool? PackageRebootIfRequired { get; init; }

    /// <summary>Linux Snap configuration. Accepted; no-op on Windows.</summary>
    public object? Snap { get; init; }

    /// <summary>Linux YUM repos. Accepted; no-op on Windows.</summary>
    public object? YumRepos { get; init; }

    public string? YumRepoDir { get; init; }

    /// <summary>Linux disk/partition setup directives. Accepted; we have <c>growpart</c> for our use case.</summary>
    public object? DiskSetup { get; init; }

    /// <summary>Linux filesystem setup directives. Accepted; no-op on Windows.</summary>
    public object? FsSetup { get; init; }

    /// <summary>Linux mount points. Accepted; no-op on Windows.</summary>
    public object? Mounts { get; init; }

    /// <summary><c>true</c>/<c>false</c>/<c>localhost</c>/<c>template</c>. Linux /etc/hosts management. Accepted; no-op on Windows (Windows manages hosts differently).</summary>
    public object? ManageEtcHosts { get; init; }

    public bool? ManageResolvConf { get; init; }

    /// <summary>Linux /etc/resolv.conf contents. Accepted; ApplyNetworkConfig handles DNS on Windows.</summary>
    public object? ResolvConf { get; init; }

    /// <summary>Runs on every boot, very early. Cloud-init has <c>bootcmd</c>; we don't yet — accepted as no-op.</summary>
    public object? Bootcmd { get; init; }

    /// <summary>
    /// Cloud-init compatible end-of-provisioning reboot/poweroff directive.
    /// Handled by <c>PowerStateModule</c> (cf. RFC 0024). NOT an
    /// acknowledged-but-no-op key — actual semantics apply.
    /// </summary>
    public PowerStateConfig? PowerState { get; init; }

    /// <summary>POST instance metadata to a URL at end of provisioning. Not yet implemented.</summary>
    public object? PhoneHome { get; init; }

    /// <summary>Custom message printed at end of provisioning. Not yet implemented.</summary>
    public string? FinalMessage { get; init; }

    /// <summary>CA certificate installation directives. Not yet implemented (Windows cert store is different from Linux).</summary>
    public object? CaCerts { get; init; }

    public bool? DisableRoot { get; init; }

    public string? DisableRootOpts { get; init; }

    /// <summary>Chef bootstrap configuration. Future module. Accepted; no-op today.</summary>
    public object? Chef { get; init; }

    /// <summary>Ansible pull / push configuration. Future module. Accepted; no-op today.</summary>
    public object? Ansible { get; init; }

    /// <summary>Puppet agent configuration. Future module. Accepted; no-op today.</summary>
    public object? Puppet { get; init; }

    /// <summary>Salt minion configuration. Future module. Accepted; no-op today.</summary>
    public object? SaltMinion { get; init; }
}
