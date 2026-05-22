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
    Task SetUserSshAuthorizedKeysAsync(
        string userName,
        IReadOnlyList<string> keys,
        CancellationToken cancellationToken);

    // Commands

    Task<RunCommandResult> RunShellCommandAsync(string command, CancellationToken cancellationToken);

    Task<RunCommandResult> RunArgvCommandAsync(IReadOnlyList<string> argv, CancellationToken cancellationToken);

    // Paths

    /// <summary>
    /// Translates a cloud-init style unix path to a Windows path. See the
    /// implementation header for the exact mapping.
    /// </summary>
    string TranslateUnixPath(string unixPath);

    // Networking — used by ApplyNetworkConfigModule. See RFC 0002.

    /// <summary>
    /// Enumerates the network adapters on this guest. The result is intended
    /// for MAC-matching against cloud-init network-config and includes both
    /// physical and virtual adapters; callers should filter on
    /// <see cref="NetworkAdapterInfo.IsPhysical"/> when only hardware NICs
    /// are relevant.
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
    /// Sets the MTU on the given adapter (IPv4 + IPv6 interface). No-op when
    /// <paramref name="mtu"/> already matches the current value.
    /// </summary>
    Task SetInterfaceMtuAsync(
        int interfaceIndex,
        int mtu,
        CancellationToken cancellationToken);
}
