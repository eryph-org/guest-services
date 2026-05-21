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
}
