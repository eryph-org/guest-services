using Eryph.GuestServices.CloudConfig;
using Eryph.GuestServices.Core;

namespace Eryph.GuestServices.Provisioning.Windows;

/// <summary>
/// Pure abstraction over the Windows guest OS operations needed by the
/// provisioning handlers. The concrete implementation lives in
/// <see cref="WindowsOs"/>; tests substitute this interface.
/// </summary>
public interface IWindowsOs
{
    // Hostname

    Task<string> GetComputerNameAsync(CancellationToken cancellationToken);

    Task<SetComputerNameResult> SetComputerNameAsync(string newName, CancellationToken cancellationToken);

    // Users / groups

    Task<bool> LocalUserExistsAsync(string name, CancellationToken cancellationToken);

    Task CreateLocalUserAsync(LocalUserSpec spec, CancellationToken cancellationToken);

    Task UpdateLocalUserAsync(LocalUserSpec spec, CancellationToken cancellationToken);

    Task SetLocalUserPasswordAsync(
        string name,
        string password,
        bool mustChangeAtNextLogon,
        CancellationToken cancellationToken);

    Task<bool> LocalGroupExistsAsync(string name, CancellationToken cancellationToken);

    Task CreateLocalGroupAsync(string name, CancellationToken cancellationToken);

    Task AddUserToGroupAsync(string userName, string groupName, CancellationToken cancellationToken);

    /// <summary>
    /// Adds the user to the local Administrators group, looked up via the
    /// well-known SID <c>S-1-5-32-544</c> so the operation works on localized
    /// Windows installs (e.g. "Administradores" on Spanish Windows).
    /// </summary>
    Task EnsureUserInAdministratorsAsync(string userName, CancellationToken cancellationToken);

    // Files

    Task EnsureDirectoryAsync(string windowsPath, CancellationToken cancellationToken);

    Task WriteFileAsync(string windowsPath, byte[] content, bool append, CancellationToken cancellationToken);

    /// <summary>
    /// Sets the owner (and optional group) of a file. Format: <c>"user"</c>,
    /// <c>"user:group"</c>. The group component is currently ignored on Windows.
    /// </summary>
    Task SetFileOwnerAsync(string windowsPath, string owner, CancellationToken cancellationToken);

    /// <summary>
    /// Translates a POSIX octal permission string (e.g. <c>"0644"</c>) into
    /// NTFS ACEs for owner / group / Everyone and applies them to the file.
    /// SYSTEM and Administrators retain FullControl (they're added/preserved
    /// in the DACL) so the agent and system processes can always read the
    /// file regardless of the POSIX bits. Cloudbase-init does the same.
    /// </summary>
    Task SetPosixPermissionsAsync(
        string windowsPath,
        string permissions,
        string? owner,
        CancellationToken cancellationToken);

    // SSH authorized keys

    /// <summary>
    /// For users in the local Administrators group writes the keys to
    /// <c>%ProgramData%\ssh\administrators_authorized_keys</c> (per the
    /// Windows OpenSSH rules). For other users writes to
    /// <c>C:\Users\&lt;user&gt;\.ssh\authorized_keys</c>.
    /// </summary>
    /// <remarks>
    /// MERGE semantics (RFC 0018): the supplied keys are unioned with the
    /// existing file content, deduplicated by the normalized key body
    /// (<c>&lt;type&gt; &lt;base64&gt;</c>, ignoring the trailing comment).
    /// Existing line order is preserved; genuinely new keys are appended.
    /// An empty input with no existing file is a no-op (no empty file is
    /// created). ACL hardening is re-applied after the write.
    /// </remarks>
    Task SetUserSshAuthorizedKeysAsync(
        string userName,
        IReadOnlyList<string> keys,
        CancellationToken cancellationToken);

    // OpenSSH daemon (Win32-OpenSSH at C:\ProgramData\ssh) — used by SshModule.
    // RFC 0018. NOT the egs-service Hyper-V-socket transport.

    /// <summary>
    /// True when the Win32-OpenSSH <c>sshd</c> service is installed (its
    /// Win32_Service entry exists, regardless of run state).
    /// </summary>
    Task<bool> IsSshdInstalledAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Installs the OpenSSH server capability
    /// (<c>Add-WindowsCapability -Online -Name OpenSSH.Server~~~~0.0.1.0</c>)
    /// and sets the <c>sshd</c> service start mode to Automatic. No-op when
    /// sshd is already installed.
    /// </summary>
    Task InstallOpenSshServerAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Generates ssh host keys for each requested type (validated against
    /// <c>{ed25519, ecdsa, rsa}</c>; <c>dsa</c> / unknown types are warned and
    /// skipped). RSA is generated at 3072 bits. When
    /// <paramref name="deleteExisting"/> is true the existing
    /// <c>ssh_host_*_key{,.pub}</c> files are removed first. Each private key
    /// is ACL-hardened. Returns the fingerprints of the keys produced.
    /// </summary>
    Task<IReadOnlyList<SshHostKeyFingerprint>> RegenerateSshHostKeysAsync(
        IReadOnlyList<string> keyTypes,
        bool deleteExisting,
        CancellationToken cancellationToken);

    /// <summary>
    /// Writes an operator-supplied host key:
    /// <c>C:\ProgramData\ssh\ssh_host_&lt;keyType&gt;_key</c> (private PEM) and,
    /// when <paramref name="publicLine"/> is supplied, the matching
    /// <c>.pub</c>. The private key is ACL-hardened (owner SYSTEM;
    /// SYSTEM + Administrators FullControl).
    /// </summary>
    Task WriteSshHostKeyAsync(
        string keyType,
        string privatePem,
        string? publicLine,
        CancellationToken cancellationToken);

    /// <summary>
    /// Idempotently ensures <c>C:\ProgramData\ssh\sshd_config</c> contains an
    /// uncommented <c>Include sshd_config.d/*.conf</c> directive at the top
    /// (so our drop-in's first-obtained-value settings win over shipped
    /// directives). Creates the <c>sshd_config.d</c> directory.
    /// </summary>
    Task EnsureSshdConfigIncludeAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Writes a drop-in config file under
    /// <c>C:\ProgramData\ssh\sshd_config.d\&lt;dropInFileName&gt;</c> (UTF-8,
    /// LF line endings).
    /// </summary>
    Task WriteSshdDropInAsync(
        string dropInFileName,
        string contents,
        CancellationToken cancellationToken);

    /// <summary>
    /// Restarts the <c>sshd</c> service (stop + start) so it re-reads its
    /// config. Tolerates sshd not being installed (logs and returns).
    /// </summary>
    Task RestartSshdAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Resolves the on-disk name of the built-in Administrator account
    /// (RID 500) via its SID so the lookup survives a rename. Falls back to
    /// <c>"Administrator"</c> (with a Warning) when the SID cannot be resolved.
    /// </summary>
    Task<string> ResolveBuiltinAdministratorNameAsync(CancellationToken cancellationToken);

    // Commands

    Task<RunCommandResult> RunShellCommandAsync(string command, CancellationToken cancellationToken);

    Task<RunCommandResult> RunArgvCommandAsync(IReadOnlyList<string> argv, CancellationToken cancellationToken);

    /// <summary>
    /// As <see cref="RunShellCommandAsync(string, CancellationToken)"/>, but
    /// also injects the given key/value pairs into the child's environment
    /// (in addition to the inherited parent environment). Used by
    /// <c>RuncmdModule</c> and <c>ScriptsUserModule</c> to surface the
    /// <c>EGS_ENTRY_INDEX</c>, <c>EGS_REBOOT_COUNT</c>, and
    /// <c>EGS_REBOOT_LIMIT</c> variables so the child can read its current
    /// reboot count and limit.
    /// </summary>
    Task<RunCommandResult> RunShellCommandAsync(
        string command,
        IReadOnlyDictionary<string, string> environment,
        CancellationToken cancellationToken);

    /// <summary>
    /// As <see cref="RunArgvCommandAsync(IReadOnlyList{string}, CancellationToken)"/>,
    /// but also injects the given key/value pairs into the child's
    /// environment (in addition to the inherited parent environment).
    /// </summary>
    Task<RunCommandResult> RunArgvCommandAsync(
        IReadOnlyList<string> argv,
        IReadOnlyDictionary<string, string> environment,
        CancellationToken cancellationToken);

    // Paths

    /// <summary>
    /// Translates a cloud-init style unix path to a Windows path. See the
    /// implementation header for the exact mapping.
    /// </summary>
    string TranslateUnixPath(string unixPath);

    /// <summary>
    /// Expands environment-variable references (e.g. <c>%ProgramData%</c>) in
    /// <paramref name="value"/>. Routed through the OS abstraction so modules
    /// stay host-OS independent — <c>Environment.ExpandEnvironmentVariables</c>
    /// is a no-op on non-Windows hosts, which silently breaks paths during
    /// tests. The production implementation calls the Win32 expander.
    /// </summary>
    string ExpandEnvironmentVariables(string value);

    /// <summary>
    /// Returns the system-drive letter (e.g. <c>'C'</c>) resolved from
    /// <c>%SystemDrive%</c>, uppercased. Null when it cannot be resolved.
    /// Routed through the OS abstraction so the value is mockable and the
    /// resolution does not depend on the host's environment.
    /// </summary>
    char? GetSystemDriveLetter();

    // Networking — used by ApplyNetworkConfigModule. See RFC 0002.

    /// <summary>
    /// Enumerates the MAC-bearing network adapters on this guest, for
    /// MAC-matching against cloud-init network-config. Loopback/tunnel
    /// interfaces and adapters without a usable hardware address are excluded.
    /// </summary>
    Task<IReadOnlyList<NetworkAdapterInfo>> GetNetworkAdaptersAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Puts the IPv4 client on the given adapter back into DHCP mode and
    /// removes any explicit static addresses / gateways carried over from a
    /// previous static configuration. Idempotent.
    /// </summary>
    Task EnableDhcpAsync(int interfaceIndex, CancellationToken cancellationToken);

    /// <summary>
    /// Switches the IPv4 client on the given adapter to manual addressing and
    /// removes any DHCP-leased addresses. Idempotent — calling twice is a no-op.
    /// </summary>
    Task DisableDhcpAsync(int interfaceIndex, CancellationToken cancellationToken);

    /// <summary>
    /// Sets the IPv4 addresses on the given adapter so that the resulting set
    /// equals <paramref name="addresses"/>. Each entry is a CIDR string (e.g.
    /// <c>"10.0.0.5/24"</c>). Existing addresses that are not in the desired
    /// set are removed. Implies DHCP-off for IPv4.
    /// </summary>
    Task SetStaticIpv4AddressesAsync(
        int interfaceIndex,
        IReadOnlyList<string> addresses,
        CancellationToken cancellationToken);

    /// <summary>
    /// Sets the IPv4 default gateway on the given adapter. Removes any
    /// pre-existing default route on the same interface so the result is the
    /// single requested gateway. Pass null to clear the default route.
    /// </summary>
    Task SetIpv4DefaultGatewayAsync(
        int interfaceIndex,
        string? gateway,
        CancellationToken cancellationToken);

    /// <summary>
    /// Sets the DNS server list on the given adapter. Empty list resets the
    /// adapter to "obtain DNS automatically" (DHCP-driven). Address family is
    /// inferred from each entry.
    /// </summary>
    Task SetDnsServersAsync(
        int interfaceIndex,
        IReadOnlyList<string> dnsServers,
        CancellationToken cancellationToken);

    /// <summary>
    /// IPv6 counterpart to <see cref="EnableDhcpAsync"/>: turns DHCPv6 on for
    /// the adapter and removes any explicit static IPv6 addresses carried over
    /// from a previous run. Idempotent.
    /// </summary>
    Task EnableDhcp6Async(int interfaceIndex, CancellationToken cancellationToken);

    /// <summary>
    /// IPv6 counterpart to <see cref="DisableDhcpAsync"/>: switches the IPv6
    /// client on the given adapter to manual addressing and removes any
    /// DHCPv6-leased addresses. Idempotent.
    /// </summary>
    Task DisableDhcp6Async(int interfaceIndex, CancellationToken cancellationToken);

    /// <summary>
    /// Sets the IPv6 addresses on the given adapter so that the resulting set
    /// equals <paramref name="addresses"/>. Each entry is a CIDR string (e.g.
    /// <c>"2001:db8::1/64"</c>). Existing addresses that are not in the desired
    /// set are removed. Implies DHCPv6-off for the adapter.
    /// </summary>
    Task SetStaticIpv6AddressesAsync(
        int interfaceIndex,
        IReadOnlyList<string> addresses,
        CancellationToken cancellationToken);

    /// <summary>
    /// Sets the IPv6 default gateway on the given adapter. Removes any
    /// pre-existing default route on the same interface so the result is the
    /// single requested gateway. Pass null to clear the default route.
    /// </summary>
    Task SetIpv6DefaultGatewayAsync(
        int interfaceIndex,
        string? gateway,
        CancellationToken cancellationToken);

    /// <summary>
    /// Applies the per-interface route list. Address family is inferred from
    /// each route's destination prefix (<c>":"</c> => IPv6, else IPv4). The
    /// implementation removes any existing routes on the interface that match
    /// a destination we're about to add (so re-runs with the same list produce
    /// the same end state). Empty list is a no-op.
    /// </summary>
    Task SetInterfaceRoutesAsync(
        int interfaceIndex,
        IReadOnlyList<NetworkRoute> routes,
        CancellationToken cancellationToken);

    /// <summary>
    /// Applies cloud-init's per-interface DNS <c>search</c> list. On Windows
    /// the model is a compromise:
    /// <list type="bullet">
    ///   <item>The first entry becomes the connection-specific suffix on the
    ///   interface (the only per-NIC suffix Windows supports).</item>
    ///   <item>Every entry is merged (deduped, order-preserving) into the
    ///   machine-wide <c>SuffixSearchList</c> so resolvers actually consult them.</item>
    /// </list>
    /// Empty list is a no-op (we do NOT clear an operator-tuned search list).
    /// </summary>
    Task SetDnsSearchSuffixesAsync(
        int interfaceIndex,
        IReadOnlyList<string> searchDomains,
        CancellationToken cancellationToken);

    /// <summary>
    /// Sets the MTU on the given adapter (IPv4 + IPv6 interface). No-op when
    /// <paramref name="mtu"/> already matches the current value.
    /// </summary>
    Task SetInterfaceMtuAsync(
        int interfaceIndex,
        int mtu,
        CancellationToken cancellationToken);

    // Storage — used by GrowpartModule. Mirrors cloudbase-init's extend_volumes
    // via the WSM (root\Microsoft\Windows\Storage) WMI namespace.

    /// <summary>
    /// Refreshes every disk's geometry (so a guest that booted after the host
    /// enlarged the underlying VHD picks up the new size — the GPT secondary
    /// header migration cbi does not perform) and then resizes every targeted
    /// partition to the maximum size the platform supports.
    /// <para>
    /// <paramref name="driveLetterFilter"/> is null when the caller wants every
    /// growable volume; otherwise it lists uppercase drive letters to target.
    /// Volumes without a drive letter are only considered when the filter is null.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<VolumeExtendResult>> ExtendVolumesAsync(
        IReadOnlySet<char>? driveLetterFilter,
        CancellationToken cancellationToken);

    // Time service — used by NtpClientModule. Mirrors cloudbase-init's
    // NTPClientPlugin: w32time service auto-start + manual peer list.

    /// <summary>
    /// Configures the Windows Time service (<c>w32time</c>). When
    /// <paramref name="enabled"/> is true the service is set to
    /// <c>Automatic</c>, started, the SCM triggers are reset so it follows
    /// network availability (cloudbase-init parity), and the manual peer
    /// list is set to the union of <paramref name="peers"/>. When false
    /// the service is stopped and set to <c>Disabled</c>.
    /// </summary>
    Task ConfigureNtpClientAsync(
        bool enabled,
        IReadOnlyList<string> peers,
        CancellationToken cancellationToken);

    /// <summary>
    /// Writes
    /// <c>HKLM\SYSTEM\CurrentControlSet\Control\TimeZoneInformation\RealTimeIsUniversal</c>.
    /// <c>true</c> tells Windows to interpret the real-time clock as UTC;
    /// <c>false</c> means "local time" (Windows default). Mirrors
    /// cloudbase-init's <c>set_real_time_clock_utc</c>.
    /// </summary>
    Task SetRealTimeClockUtcAsync(bool utc, CancellationToken cancellationToken);

    // Timezone — used by TimezoneModule. <paramref name="windowsTimezoneId"/>
    // is a Windows timezone key name (e.g. "W. Europe Standard Time"). The
    // module is responsible for translating an IANA name into the Windows id.

    Task SetTimezoneAsync(string windowsTimezoneId, CancellationToken cancellationToken);

    // Locale + keyboard — used by SetLocaleModule. ApplyLocaleAsync returns
    // a struct indicating whether a reboot is needed (set-WinSystemLocale
    // requires it; the other changes do not).

    Task<LocaleApplyResult> ApplyLocaleAsync(LocaleSpec spec, CancellationToken cancellationToken);

    // Licensing — used by LicensingModule. Sets the product key and/or KMS
    // host via slmgr.vbs and optionally triggers activation. Throws on
    // slmgr non-zero exit; the module surfaces failures as ModuleOutcome.Failed.

    Task ApplyLicenseAsync(LicenseSpec spec, CancellationToken cancellationToken);

    /// <summary>
    /// Runs <c>slmgr /rearm</c>. Returns whether a reboot is required (per
    /// Microsoft docs the answer is always "yes" after a successful rearm,
    /// but the OS layer is authoritative — the module just relays).
    /// </summary>
    Task<RearmResult> RearmLicenseAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Resolves the volume-activation key for the guest's current OS
    /// edition. Returns null when no key is known (unsupported OS family,
    /// non-Server SKU, or pre-2012R2 for AVMA). The module decides what to
    /// do with a null result — usually surface as failure with a hint.
    /// </summary>
    Task<string?> ResolveVolumeActivationKeyAsync(
        VolumeActivationKeyType type,
        CancellationToken cancellationToken);

    /// <summary>
    /// True when the active Windows product is an evaluation edition (its
    /// SoftwareLicensingProduct entry carries TIMEBASED_EVAL or has a real
    /// EvaluationEndDate). Used by the licensing module to gate
    /// <c>slmgr /rearm</c> so non-eval guests don't burn a rearm slot.
    /// </summary>
    Task<bool> IsEvaluationLicenseAsync(CancellationToken cancellationToken);

    // Power-state — used by PowerStateModule. Schedules a controlled
    // shutdown / reboot / hibernate at the end of provisioning via
    // shutdown.exe. The OS layer just executes; the module does delay
    // parsing / condition evaluation / mode mapping.

    Task RequestPowerStateAsync(PowerStateRequest request, CancellationToken cancellationToken);

    // egs agent control — used by EgsModule. Writes the opt-out capability
    // switches read at service start by IServiceControlFlags.

    /// <summary>
    /// Writes one guest-services capability switch to the platform-native
    /// control surface (Windows: a REG_DWORD under
    /// <c>HKLM\SOFTWARE\eryph\guest-services</c>; <c>true</c> = <c>1</c>,
    /// <c>false</c> = <c>0</c>). The value is read at the next service start,
    /// so a change made during provisioning takes effect after a restart.
    /// </summary>
    Task SetServiceControlFlagAsync(
        ServiceControlFlag flag,
        bool enabled,
        CancellationToken cancellationToken);
}
