using Eryph.GuestServices.CloudConfig.Linux;

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

    /// <summary>
    /// Cloud-init <c>prefer_fqdn_over_hostname</c>. When true, hostname-
    /// applying modules use the FQDN form. Cross-platform: the runtime
    /// SetHostnameModule wires this up in Phase 3.
    /// </summary>
    [CloudInitField]
    public bool? PreferFqdnOverHostname { get; init; }

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
    // act on. CloudConfigSerializer walks these after parsing and emits one
    // Info line per non-null entry — operators get a clear "we saw your
    // apt: block, it's a Linux concept, safely ignored" signal instead of
    // either a Warning or silent drop.
    //
    // These exist primarily so cross-cloud cloud-config YAML written for
    // Linux + Windows guests round-trips through the parser without
    // operator-visible noise on the keys that have no Windows analogue.
    // Phase 2 typed every Linux key against cloud-init's documented schema
    // so the structure survives merges and YAML round-trips.
    // ---------------------------------------------------------------------

    /// <summary>Linux APT package source configuration. Accepted; no-op on Windows.</summary>
    [CloudInitField(Platforms = CloudInitPlatforms.Linux, Description = "Linux APT package source configuration; no-op on Windows")]
    public AptConfig? Apt { get; init; }

    /// <summary>Linux APT pipelining flag. Accepted; no-op on Windows.</summary>
    [CloudInitField(Platforms = CloudInitPlatforms.Linux, Description = "Linux APT pipelining; no-op on Windows")]
    public object? AptPipelining { get; init; }

    /// <summary>Linux package list. Future: may map to chocolatey / winget. Accepted; no-op today.</summary>
    [CloudInitField(Platforms = CloudInitPlatforms.Linux, Description = "Linux package list (no Windows package-manager binding yet)")]
    public object? Packages { get; init; }

    [CloudInitField(Platforms = CloudInitPlatforms.Linux, Description = "Linux package-manager refresh; no-op on Windows")]
    public bool? PackageUpdate { get; init; }

    [CloudInitField(Platforms = CloudInitPlatforms.Linux, Description = "Linux package-manager upgrade; no-op on Windows")]
    public bool? PackageUpgrade { get; init; }

    [CloudInitField(Platforms = CloudInitPlatforms.Linux, Description = "Linux post-upgrade reboot trigger; no-op on Windows")]
    public bool? PackageRebootIfRequired { get; init; }

    /// <summary>Linux Snap configuration. Accepted; no-op on Windows.</summary>
    [CloudInitField(Platforms = CloudInitPlatforms.Linux, Description = "Linux Snap configuration; no-op on Windows")]
    public SnapConfig? Snap { get; init; }

    /// <summary>Linux YUM repos keyed by .repo file name. Accepted; no-op on Windows.</summary>
    [CloudInitField(Platforms = CloudInitPlatforms.Linux, Description = "Linux YUM repositories; no-op on Windows")]
    public IReadOnlyDictionary<string, YumRepoConfig>? YumRepos { get; init; }

    [CloudInitField(Platforms = CloudInitPlatforms.Linux, Description = "Linux YUM repo directory; no-op on Windows")]
    public string? YumRepoDir { get; init; }

    /// <summary>Linux disk/partition setup directives keyed by device path. Accepted; we have <c>growpart</c> for our use case.</summary>
    [CloudInitField(Platforms = CloudInitPlatforms.Linux, Description = "Linux disk-partition setup (use 'growpart' on Windows)")]
    public IReadOnlyDictionary<string, DiskSetupEntry>? DiskSetup { get; init; }

    /// <summary>Linux filesystem setup directives. Accepted; no-op on Windows.</summary>
    [CloudInitField(Platforms = CloudInitPlatforms.Linux, Description = "Linux filesystem-setup directives; no-op on Windows")]
    public IReadOnlyList<FsSetupEntry>? FsSetup { get; init; }

    /// <summary>
    /// Linux mount points — list of 6-element fstab-shaped lists
    /// <c>[device, mountpoint, fstype, opts, freq, passno]</c>. Accepted;
    /// no-op on Windows.
    /// </summary>
    [CloudInitField(Platforms = CloudInitPlatforms.Linux, Description = "Linux mount points; no-op on Windows")]
    public IReadOnlyList<IReadOnlyList<string>>? Mounts { get; init; }

    /// <summary>
    /// Linux <c>mount_default_fields</c> — six-element default list used to
    /// fill omitted fstab columns. Accepted; no-op on Windows.
    /// </summary>
    [CloudInitField(Platforms = CloudInitPlatforms.Linux, Description = "Linux mount default fields; no-op on Windows")]
    public IReadOnlyList<string>? MountDefaultFields { get; init; }

    /// <summary>
    /// Cloud-init swap-file block. Accepted; no-op on Windows where the
    /// page file is OS-managed.
    /// </summary>
    [CloudInitField(Platforms = CloudInitPlatforms.Linux, Description = "Linux swap file; no-op on Windows")]
    public SwapConfig? Swap { get; init; }

    /// <summary>
    /// <c>true</c>/<c>false</c>/<c>localhost</c>/<c>template</c>. Linux
    /// /etc/hosts management. Accepted; no-op on Windows (Windows manages
    /// hosts differently). Type is <c>string?</c> so the YAML 1.2 resolver
    /// stringifies bool literals and the documented enum values both flow
    /// through unchanged.
    /// </summary>
    [CloudInitField(Platforms = CloudInitPlatforms.Linux, Description = "Linux /etc/hosts management; no-op on Windows")]
    public string? ManageEtcHosts { get; init; }

    [CloudInitField(Platforms = CloudInitPlatforms.Linux, Description = "Linux /etc/resolv.conf management; no-op on Windows")]
    public bool? ManageResolvConf { get; init; }

    /// <summary>Linux /etc/resolv.conf contents. Accepted; ApplyNetworkConfig handles DNS on Windows.</summary>
    [CloudInitField(Platforms = CloudInitPlatforms.Linux, Description = "Linux /etc/resolv.conf contents (use 'network-config' on Windows)")]
    public ResolvConfConfig? ResolvConf { get; init; }

    /// <summary>
    /// Runs on every boot, very early. Cloud-init has <c>bootcmd</c>; we
    /// don't yet — accepted as no-op. Same shape as <see cref="Runcmd"/>:
    /// shell-string or argv-list per entry.
    /// </summary>
    [CloudInitField(Platforms = CloudInitPlatforms.Linux, Description = "cloud-init bootcmd (not yet implemented on Windows)")]
    public IReadOnlyList<RuncmdEntry>? Bootcmd { get; init; }

    /// <summary>
    /// Cloud-init compatible end-of-provisioning reboot/poweroff directive.
    /// Handled by <c>PowerStateModule</c> (cf. RFC 0024). NOT an
    /// acknowledged-but-no-op key — actual semantics apply.
    /// </summary>
    [CloudInitField]
    public PowerStateConfig? PowerState { get; init; }

    /// <summary>POST instance metadata to a URL at end of provisioning. Not yet implemented.</summary>
    [CloudInitField(Platforms = CloudInitPlatforms.Linux, Description = "cloud-init phone_home (not yet implemented)")]
    public PhoneHomeConfig? PhoneHome { get; init; }

    /// <summary>Custom message printed at end of provisioning. Not yet implemented.</summary>
    [CloudInitField(Platforms = CloudInitPlatforms.Linux, Description = "cloud-init final_message (not yet implemented)")]
    public string? FinalMessage { get; init; }

    /// <summary>CA certificate installation directives. Not yet implemented (Windows cert store is different from Linux).</summary>
    [CloudInitField(Platforms = CloudInitPlatforms.Linux, Description = "CA certificate installation (not yet implemented; Windows cert store differs)")]
    public CaCertsConfig? CaCerts { get; init; }

    [CloudInitField(Platforms = CloudInitPlatforms.Linux, Description = "Linux root account management; no-op on Windows")]
    public bool? DisableRoot { get; init; }

    [CloudInitField(Platforms = CloudInitPlatforms.Linux, Description = "Linux root account management; no-op on Windows")]
    public string? DisableRootOpts { get; init; }

    /// <summary>Chef bootstrap configuration. Future module. Accepted; no-op today.</summary>
    [CloudInitField(Platforms = CloudInitPlatforms.Linux, Description = "Chef bootstrap (future)")]
    public ChefConfig? Chef { get; init; }

    /// <summary>Ansible pull / push configuration. Future module. Accepted; no-op today.</summary>
    [CloudInitField(Platforms = CloudInitPlatforms.Linux, Description = "Ansible bootstrap (future)")]
    public AnsibleConfig? Ansible { get; init; }

    /// <summary>Puppet agent configuration. Future module. Accepted; no-op today.</summary>
    [CloudInitField(Platforms = CloudInitPlatforms.Linux, Description = "Puppet bootstrap (future)")]
    public PuppetConfig? Puppet { get; init; }

    /// <summary>Salt minion configuration. Future module. Accepted; no-op today.</summary>
    [CloudInitField(Platforms = CloudInitPlatforms.Linux, Description = "Salt minion bootstrap (future)")]
    public SaltMinionConfig? SaltMinion { get; init; }

    // ---------------------------------------------------------------------
    // Linux-only top-level scalars and small configs (Phase 2 additions).
    // ---------------------------------------------------------------------

    /// <summary>When true, cloud-init disables EC2 metadata polling.</summary>
    [CloudInitField(Platforms = CloudInitPlatforms.Linux, Description = "Linux EC2 metadata polling toggle; no-op on Windows")]
    public bool? DisableEc2Metadata { get; init; }

    /// <summary>
    /// When false, cloud-init does not migrate cached state across boots
    /// (used after first-boot AMI snapshots).
    /// </summary>
    [CloudInitField(Platforms = CloudInitPlatforms.Linux, Description = "Linux cloud-init state migration; no-op on Windows")]
    public bool? Migrate { get; init; }

    /// <summary>
    /// When true, cloud-init regenerates ssh host keys at boot. YAML key is
    /// the literal <c>ssh_deletekeys</c> — one concatenated word per cloud-
    /// init's documented schema, not the snake-cased <c>ssh_delete_keys</c>
    /// the naming convention would produce.
    /// </summary>
    [CloudInitField(Platforms = CloudInitPlatforms.Linux, YamlName = "ssh_deletekeys", Description = "Linux ssh host-key regeneration; no-op on Windows")]
    public bool? SshDeleteKeys { get; init; }

    /// <summary>ssh host-key types to generate (e.g. <c>rsa</c>, <c>ed25519</c>).</summary>
    [CloudInitField(Platforms = CloudInitPlatforms.Linux, YamlName = "ssh_genkeytypes", Description = "Linux ssh host-key types; no-op on Windows")]
    public IReadOnlyList<string>? SshGenKeyTypes { get; init; }

    /// <summary>Launchpad / GitHub ssh-import-id sources.</summary>
    [CloudInitField(Platforms = CloudInitPlatforms.Linux, Description = "Linux ssh-import-id sources; no-op on Windows")]
    public IReadOnlyList<string>? SshImportId { get; init; }

    /// <summary>
    /// Pre-generated ssh host keys keyed by <c>&lt;type&gt;_{private,public}</c>
    /// (e.g. <c>rsa_private</c>, <c>ed25519_public</c>).
    /// </summary>
    [CloudInitField(Platforms = CloudInitPlatforms.Linux, Description = "Linux pre-generated ssh host keys; no-op on Windows")]
    public IReadOnlyDictionary<string, string>? SshKeys { get; init; }

    /// <summary>Publish ssh host keys to the platform metadata service.</summary>
    [CloudInitField(Platforms = CloudInitPlatforms.Linux, YamlName = "ssh_publish_hostkeys", Description = "Linux ssh host-key publication; no-op on Windows")]
    public SshPublishHostKeysConfig? SshPublishHostKeys { get; init; }

    /// <summary>byobu auto-launch toggle (Ubuntu only).</summary>
    [CloudInitField(Platforms = CloudInitPlatforms.Linux, Description = "Linux byobu auto-launch toggle; no-op on Windows")]
    public string? ByobuByDefault { get; init; }

    /// <summary>
    /// Resize the root filesystem at boot. Accepts <c>true</c>/<c>false</c>/
    /// <c>"noblock"</c>; modeled as <c>string?</c> so the YAML 1.2 schema
    /// resolver stringifies bool literals to <c>"True"</c>/<c>"False"</c>
    /// (or the user quotes them explicitly) without losing fidelity.
    /// </summary>
    [CloudInitField(Platforms = CloudInitPlatforms.Linux, Description = "Linux root filesystem resize toggle; no-op on Windows")]
    public string? ResizeRootfs { get; init; }

    /// <summary>Path of the locale config file (e.g. <c>/etc/default/locale</c>).</summary>
    [CloudInitField(Platforms = CloudInitPlatforms.Linux, YamlName = "locale_configfile", Description = "Linux locale config file path; no-op on Windows")]
    public string? LocaleConfigFile { get; init; }

    /// <summary>Seed the kernel RNG from cloud-config.</summary>
    [CloudInitField(Platforms = CloudInitPlatforms.Linux, Description = "Linux RNG seeding; no-op on Windows")]
    public RandomSeedConfig? RandomSeed { get; init; }

    /// <summary>
    /// Cloud-init stdout/stderr redirection configuration. Opaque pass-
    /// through — cloud-init's schema for this key is intricate (per-stage
    /// redirection with shell-pipe targets) and Windows-irrelevant.
    /// </summary>
    [CloudInitField(Platforms = CloudInitPlatforms.Linux, Description = "Linux cloud-init output redirection; no-op on Windows")]
    public IReadOnlyDictionary<string, object?>? Output { get; init; }

    /// <summary>Cloud-init reporting handlers (webhook / log / hyperv).</summary>
    [CloudInitField(Platforms = CloudInitPlatforms.Linux, Description = "Linux cloud-init reporting handlers; no-op on Windows")]
    public IReadOnlyDictionary<string, ReportingHandlerConfig>? Reporting { get; init; }

    /// <summary>When true, cloud-init updates /etc/hosts with the current hostname.</summary>
    [CloudInitField(Platforms = CloudInitPlatforms.Linux, Description = "Linux /etc/hosts update toggle; no-op on Windows")]
    public bool? UpdateEtcHosts { get; init; }

    /// <summary>When true, cloud-init refreshes the running hostname on every boot.</summary>
    [CloudInitField(Platforms = CloudInitPlatforms.Linux, Description = "Linux hostname refresh toggle; no-op on Windows")]
    public bool? UpdateHostname { get; init; }
}
