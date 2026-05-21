// Unix-to-Windows path mapping used by TranslateUnixPath:
//   /                -> C:\
//   /home/<u>/...    -> C:\Users\<u>\...
//   /root/...        -> C:\Users\Administrator\...
//   /tmp/...         -> %TEMP%\... (Path.GetTempPath())
//   /var/...         -> C:\ProgramData\... (rough analogue of /var)
//   anything else    -> C:\<rest of path, separators flipped>
// Anything already drive-rooted (e.g. C:\foo, c:/foo) is returned unchanged.

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Eryph.GuestServices.Provisioning.Windows.Win32;
using Microsoft.Extensions.Logging;

namespace Eryph.GuestServices.Provisioning.Windows;

[SupportedOSPlatform("windows")]
internal sealed class WindowsOs(ILogger<WindowsOs> logger) : IWindowsOs
{
    // Win32_ComputerSystem.Rename success codes:
    //   0 = success
    //   anything else = failure (see WMI docs)
    // The change is never effective without reboot.

    public Task<string> GetComputerNameAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult(Environment.MachineName);
    }

    public async Task<SetComputerNameResult> SetComputerNameAsync(
        string newName,
        CancellationToken cancellationToken)
    {
        var current = Environment.MachineName;
        if (string.Equals(current, newName, StringComparison.OrdinalIgnoreCase))
            return SetComputerNameResult.AlreadySet;

        await Task.Run(() =>
        {
            var rc = CimComputerSystem.Rename(newName);
            if (rc != 0)
                throw new InvalidOperationException(
                    $"Win32_ComputerSystem.Rename returned {rc} when renaming '{current}' to '{newName}'.");
        }, cancellationToken).ConfigureAwait(false);

        return SetComputerNameResult.SetWithRebootPending;
    }

    public Task<bool> LocalUserExistsAsync(string name, CancellationToken cancellationToken)
    {
        return Task.Run(() => NetUserHelpers.UserExists(name), cancellationToken);
    }

    public Task CreateLocalUserAsync(LocalUserSpec spec, CancellationToken cancellationToken)
    {
        return Task.Run(() => NetUserHelpers.AddUser(spec, initialPassword: null), cancellationToken);
    }

    public Task UpdateLocalUserAsync(LocalUserSpec spec, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            var current = NetUserHelpers.TryGetUserInfo1(spec.Name)
                ?? throw new InvalidOperationException($"User '{spec.Name}' does not exist.");

            if (spec.Comment is not null && !string.Equals(current.usri1_comment, spec.Comment, StringComparison.Ordinal))
                NetUserHelpers.SetComment(spec.Name, spec.Comment);

            if (spec.HomeDir is not null && !string.Equals(current.usri1_home_dir, spec.HomeDir, StringComparison.Ordinal))
                NetUserHelpers.SetHomeDir(spec.Name, spec.HomeDir);

            if (spec.FullName is not null)
                NetUserHelpers.SetFullName(spec.Name, spec.FullName);

            if (spec.Disabled.HasValue)
            {
                var isDisabled = (current.usri1_flags & NetApi32.UF_ACCOUNTDISABLE) != 0;
                if (isDisabled != spec.Disabled.Value)
                {
                    var newFlags = spec.Disabled.Value
                        ? current.usri1_flags | NetApi32.UF_ACCOUNTDISABLE
                        : current.usri1_flags & ~NetApi32.UF_ACCOUNTDISABLE;
                    NetUserHelpers.SetFlags(spec.Name, newFlags);
                }
            }
        }, cancellationToken);
    }

    public Task SetLocalUserPasswordAsync(
        string name,
        string password,
        bool mustChangeAtNextLogon,
        CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            NetUserHelpers.SetPassword(name, password);
            NetUserHelpers.SetPasswordExpired(name, mustChangeAtNextLogon);
        }, cancellationToken);
    }

    public Task<bool> LocalGroupExistsAsync(string name, CancellationToken cancellationToken)
    {
        return Task.Run(() => NetUserHelpers.LocalGroupExists(name), cancellationToken);
    }

    public Task CreateLocalGroupAsync(string name, CancellationToken cancellationToken)
    {
        return Task.Run(() => NetUserHelpers.CreateLocalGroup(name), cancellationToken);
    }

    public Task AddUserToGroupAsync(string userName, string groupName, CancellationToken cancellationToken)
    {
        return Task.Run(() => NetUserHelpers.AddMemberByName(groupName, userName), cancellationToken);
    }

    public Task EnsureUserInAdministratorsAsync(string userName, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            var groupName = WellKnownGroups.AdministratorsName();
            NetUserHelpers.AddMemberByName(groupName, userName);
        }, cancellationToken);
    }

    public Task EnsureDirectoryAsync(string windowsPath, CancellationToken cancellationToken)
    {
        return Task.Run(() => Directory.CreateDirectory(windowsPath), cancellationToken);
    }

    public Task WriteFileAsync(string windowsPath, byte[] content, bool append, CancellationToken cancellationToken)
    {
        var parent = Path.GetDirectoryName(windowsPath);
        if (!string.IsNullOrEmpty(parent))
            Directory.CreateDirectory(parent);

        if (append)
        {
            using var stream = new FileStream(windowsPath, FileMode.Append, FileAccess.Write, FileShare.Read);
            return stream.WriteAsync(content, 0, content.Length, cancellationToken);
        }

        return File.WriteAllBytesAsync(windowsPath, content, cancellationToken);
    }

    public Task SetFileOwnerAsync(string windowsPath, string owner, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            // cloud-init owner is "user[:group]"; group is ignored on Windows.
            var userPart = owner.Split(':', 2)[0];
            if (string.IsNullOrWhiteSpace(userPart))
                return;

            var account = new NTAccount(userPart);
            var fileInfo = new FileInfo(windowsPath);
            var security = fileInfo.GetAccessControl();
            security.SetOwner(account);
            fileInfo.SetAccessControl(security);
        }, cancellationToken);
    }

    public Task SetUserSshAuthorizedKeysAsync(
        string userName,
        IReadOnlyList<string> keys,
        CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            // OpenSSH on Windows reads authorized keys differently for users in
            // the Administrators group, so we have to branch on membership.
            var isAdmin = IsUserInAdministrators(userName);
            var keyFile = isAdmin
                ? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "ssh", "administrators_authorized_keys")
                : Path.Combine(GetUserProfileDirectory(userName), ".ssh", "authorized_keys");

            var parent = Path.GetDirectoryName(keyFile)!;
            Directory.CreateDirectory(parent);

            // LF-only line endings + UTF-8 without BOM keep OpenSSH happy.
            var content = string.Join("\n", keys);
            if (keys.Count > 0)
                content += "\n";

            var bytes = new UTF8Encoding(false).GetBytes(content);
            File.WriteAllBytes(keyFile, bytes);

            if (isAdmin)
                ApplyAdministratorsAuthorizedKeysAcl(keyFile);
            else
                ApplyUserAuthorizedKeysAcl(keyFile, userName);
        }, cancellationToken);
    }

    public async Task<RunCommandResult> RunShellCommandAsync(
        string command,
        CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo("cmd.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add(command);

        return await RunAsync(psi, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RunCommandResult> RunArgvCommandAsync(
        IReadOnlyList<string> argv,
        CancellationToken cancellationToken)
    {
        if (argv.Count == 0)
            throw new ArgumentException("argv must contain at least one element.", nameof(argv));

        var psi = new ProcessStartInfo(argv[0])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        for (var i = 1; i < argv.Count; i++)
            psi.ArgumentList.Add(argv[i]);

        return await RunAsync(psi, cancellationToken).ConfigureAwait(false);
    }

    public string TranslateUnixPath(string unixPath)
    {
        if (string.IsNullOrWhiteSpace(unixPath))
            throw new ArgumentException("Path is empty.", nameof(unixPath));

        // Pass through if the path is already drive-rooted or UNC.
        if (unixPath.Length >= 2 && unixPath[1] == ':')
            return unixPath.Replace('/', '\\');
        if (unixPath.StartsWith(@"\\", StringComparison.Ordinal))
            return unixPath;

        if (!unixPath.StartsWith('/'))
            throw new ArgumentException(
                $"Expected an absolute unix path or a drive-rooted Windows path, got '{unixPath}'.", nameof(unixPath));

        var segments = unixPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            return "C:\\";

        return segments[0] switch
        {
            "home" when segments.Length >= 2 =>
                Path.Combine(["C:\\Users", .. segments[1..]]),
            "root" =>
                Path.Combine(["C:\\Users\\Administrator", .. segments[1..]]),
            "tmp" =>
                Path.Combine([Path.GetTempPath().TrimEnd('\\'), .. segments[1..]]),
            "var" =>
                Path.Combine(
                    [Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), .. segments[1..]]),
            _ =>
                Path.Combine(["C:\\", .. segments]),
        };
    }

    private async Task<RunCommandResult> RunAsync(ProcessStartInfo psi, CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to kill cancelled process {File}.", psi.FileName);
            }

            throw;
        }

        return new RunCommandResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    private static bool IsUserInAdministrators(string userName)
    {
        var members = NetUserHelpers.GetGroupMemberNames(WellKnownGroups.AdministratorsName());
        // Members are returned as "DOMAIN\name" — compare on the trailing component.
        foreach (var entry in members)
        {
            var name = entry;
            var slashIndex = entry.IndexOf('\\');
            if (slashIndex >= 0)
                name = entry[(slashIndex + 1)..];
            if (string.Equals(name, userName, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static string GetUserProfileDirectory(string userName)
    {
        // Standard Windows profile layout. The "C:\Users\<name>" convention has
        // held since Vista; if the profile is on a different drive we will
        // surface that via the runtime registry lookup once we need it. For now
        // this is "good enough" and matches what cloudbase-init does.
        var users = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var parent = Directory.GetParent(users)?.FullName ?? @"C:\Users";
        return Path.Combine(parent, userName);
    }

    private static void ApplyAdministratorsAuthorizedKeysAcl(string path)
    {
        // OpenSSH requires that only System and the Administrators group have
        // write access; otherwise it refuses the key file.
        var info = new FileInfo(path);
        var security = info.GetAccessControl();
        security.SetAccessRuleProtection(true, false);

        // Remove any inherited rules carried over.
        var existing = security.GetAccessRules(true, false, typeof(SecurityIdentifier));
        foreach (FileSystemAccessRule rule in existing)
            security.RemoveAccessRuleSpecific(rule);

        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        security.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(admins, FileSystemRights.FullControl, AccessControlType.Allow));
        security.SetOwner(admins);

        info.SetAccessControl(security);
    }

    private static void ApplyUserAuthorizedKeysAcl(string path, string userName)
    {
        var info = new FileInfo(path);
        var security = info.GetAccessControl();
        security.SetAccessRuleProtection(true, false);

        var existing = security.GetAccessRules(true, false, typeof(SecurityIdentifier));
        foreach (FileSystemAccessRule rule in existing)
            security.RemoveAccessRuleSpecific(rule);

        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var account = new NTAccount(userName);

        security.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(admins, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(account, FileSystemRights.Read, AccessControlType.Allow));
        security.SetOwner(account);

        info.SetAccessControl(security);
    }
}
