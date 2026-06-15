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
internal sealed class WindowsOs : IWindowsOs
{
    private readonly ILogger<WindowsOs> logger;

    public WindowsOs(ILogger<WindowsOs> logger)
    {
        this.logger = logger;
        // WellKnownGroups is a static helper and can't take its own logger via DI;
        // hand it one so its one-shot fallback warning is observable.
        WellKnownGroups.SetLogger(logger);
    }

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
            // We use level 2 here (instead of level 1) because we need
            // usri2_full_name to detect whether the full name actually changed.
            var current = NetUserHelpers.TryGetUserInfo2(spec.Name)
                ?? throw new InvalidOperationException($"User '{spec.Name}' does not exist.");

            if (spec.Comment is not null && !string.Equals(current.usri2_comment, spec.Comment, StringComparison.Ordinal))
                NetUserHelpers.SetComment(spec.Name, spec.Comment);

            if (spec.HomeDir is not null && !string.Equals(current.usri2_home_dir, spec.HomeDir, StringComparison.Ordinal))
                NetUserHelpers.SetHomeDir(spec.Name, spec.HomeDir);

            if (spec.FullName is not null && !string.Equals(current.usri2_full_name, spec.FullName, StringComparison.Ordinal))
                NetUserHelpers.SetFullName(spec.Name, spec.FullName);

            if (spec.Disabled.HasValue)
            {
                var isDisabled = (current.usri2_flags & NetApi32.UF_ACCOUNTDISABLE) != 0;
                if (isDisabled != spec.Disabled.Value)
                {
                    var newFlags = spec.Disabled.Value
                        ? current.usri2_flags | NetApi32.UF_ACCOUNTDISABLE
                        : current.usri2_flags & ~NetApi32.UF_ACCOUNTDISABLE;
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

            // Read back so a future "locked out after provisioning" report is one
            // log line away: password_expired must match mustChange, and
            // acct_expires must stay TIMEQ_FOREVER (0xFFFFFFFF) — never the epoch.
            var state = NetUserHelpers.GetPasswordState(name);
            logger.LogInformation(
                "Set password for '{User}': mustChangeAtNextLogon={MustChange}, password_expired={PasswordExpired}, acct_expires=0x{AcctExpires:X8}.",
                name, mustChangeAtNextLogon, state.passwordExpired, state.acctExpires);
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

    public async Task WriteFileAsync(string windowsPath, byte[] content, bool append, CancellationToken cancellationToken)
    {
        var parent = Path.GetDirectoryName(windowsPath);
        if (!string.IsNullOrEmpty(parent))
            Directory.CreateDirectory(parent);

        if (append)
        {
            // `await using` makes sure the stream is disposed *after* the
            // async write completes; the previous `using` + returned Task
            // disposed the stream before WriteAsync had a chance to finish.
            await using var stream = new FileStream(windowsPath, FileMode.Append, FileAccess.Write, FileShare.Read);
            await stream.WriteAsync(content.AsMemory(0, content.Length), cancellationToken).ConfigureAwait(false);
            return;
        }

        await File.WriteAllBytesAsync(windowsPath, content, cancellationToken).ConfigureAwait(false);
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

    public Task SetPosixPermissionsAsync(
        string windowsPath,
        string permissions,
        string? owner,
        CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            var (ownerBits, groupBits, otherBits) = PosixPermissions.Parse(permissions);

            var fileInfo = new FileInfo(windowsPath);
            var security = fileInfo.GetAccessControl();

            // Disable inheritance and copy existing rules so we start from a
            // known state — mirrors cloudbase-init's approach (otherwise the
            // POSIX intent is silently overridden by inherited perms).
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

            // Drop any existing explicit ACEs so the POSIX bits are authoritative.
            foreach (FileSystemAccessRule rule in security.GetAccessRules(true, false, typeof(SecurityIdentifier)))
                security.RemoveAccessRuleSpecific(rule);

            // SYSTEM and Administrators always keep FullControl — without them
            // the agent itself, defender, etc. cannot read or back up the file.
            var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            security.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(admins, FileSystemRights.FullControl, AccessControlType.Allow));

            // Owner ACE: resolved from the cloud-config `owner` field if present,
            // otherwise from the current NTFS file owner.
            IdentityReference ownerIdentity = !string.IsNullOrWhiteSpace(owner)
                ? new NTAccount(owner!.Split(':', 2)[0])
                : security.GetOwner(typeof(NTAccount))
                  ?? new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);

            var ownerRights = PosixPermissions.TripletToRights(ownerBits);
            if (ownerRights != 0)
                security.AddAccessRule(new FileSystemAccessRule(ownerIdentity, ownerRights, AccessControlType.Allow));

            // No POSIX group on Windows; map the "group" triplet to Users when
            // any rights are granted. This matches cloudbase-init's compromise.
            var groupRights = PosixPermissions.TripletToRights(groupBits);
            if (groupRights != 0)
            {
                var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
                security.AddAccessRule(new FileSystemAccessRule(users, groupRights, AccessControlType.Allow));
            }

            var otherRights = PosixPermissions.TripletToRights(otherBits);
            if (otherRights != 0)
            {
                var everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
                security.AddAccessRule(new FileSystemAccessRule(everyone, otherRights, AccessControlType.Allow));
            }

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

            // MERGE (Finding 6): start from the existing file (if any) and union
            // the new keys in, deduplicating by normalized key body. Overwriting
            // would clobber keys an image / a prior run had already installed.
            var existingLines = File.Exists(keyFile)
                ? File.ReadAllText(keyFile, new UTF8Encoding(false))
                    .Split('\n')
                    .Select(l => l.TrimEnd('\r'))
                    .ToList()
                : [];

            var merged = MergeAuthorizedKeys(existingLines, keys);

            // Empty input + no existing file → no-op. Don't create empty files
            // (sshd treats a 0-byte file as "no keys" but writing one is noise
            // and would trigger needless ACL churn).
            if (merged.Count == 0)
                return;

            var parent = Path.GetDirectoryName(keyFile)!;
            Directory.CreateDirectory(parent);

            // LF-only line endings + UTF-8 without BOM keep OpenSSH happy.
            var content = string.Join("\n", merged) + "\n";
            var bytes = new UTF8Encoding(false).GetBytes(content);
            File.WriteAllBytes(keyFile, bytes);

            if (isAdmin)
                ApplyAdministratorsAuthorizedKeysAcl(keyFile);
            else
                ApplyUserAuthorizedKeysAcl(keyFile, userName);
        }, cancellationToken);
    }

    // internal for unit testing — the merge/dedup logic is the heart of the
    // Finding 6 fix and we want to exercise it without touching the filesystem.
    internal static IReadOnlyList<string> MergeAuthorizedKeys(
        IReadOnlyList<string> existingLines,
        IReadOnlyList<string> newKeys)
    {
        var result = new List<string>();
        // Track the normalized body of every key already emitted so we never
        // write the same key twice — even when it carries a different comment.
        var seenBodies = new HashSet<string>(StringComparer.Ordinal);

        void Add(string rawLine)
        {
            // Preserve blank lines and comment lines from the existing file
            // verbatim (they are not keys; dedup does not apply).
            var trimmed = rawLine.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                // Only carry over existing-file blanks/comments, not synthetic ones.
                result.Add(rawLine);
                return;
            }

            var body = NormalizeKeyBody(trimmed);
            if (!seenBodies.Add(body))
                return; // duplicate key body — skip
            result.Add(trimmed);
        }

        // Existing lines first (order-preserving), then genuinely-new keys.
        foreach (var line in existingLines)
        {
            // Drop trailing blank/comment-only lines coming from a previous
            // file's terminating newline; keep interior content as-is.
            if (line.Trim().Length == 0)
                continue;
            Add(line);
        }

        foreach (var key in newKeys)
        {
            if (string.IsNullOrWhiteSpace(key))
                continue;
            Add(key);
        }

        return result;
    }

    // The dedup key is the "<type> <base64>" portion. authorized_keys lines may
    // carry leading options, a trailing comment, or both; we key on the type +
    // base64 blob so the same public key with a different comment is recognised
    // as a duplicate. Lines we can't parse fall back to the whole trimmed line.
    private static string NormalizeKeyBody(string line)
    {
        var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        // Find the token that looks like a key type (starts with "ssh-",
        // "ecdsa-", or "sk-") and take it plus the following base64 token.
        for (var i = 0; i < parts.Length - 1; i++)
        {
            var t = parts[i];
            if (t.StartsWith("ssh-", StringComparison.Ordinal)
                || t.StartsWith("ecdsa-", StringComparison.Ordinal)
                || t.StartsWith("sk-", StringComparison.Ordinal))
            {
                return parts[i] + " " + parts[i + 1];
            }
        }
        return line;
    }

    public Task<RunCommandResult> RunShellCommandAsync(
        string command,
        CancellationToken cancellationToken) =>
        RunShellCommandAsync(command, EmptyEnvironment, cancellationToken);

    public async Task<RunCommandResult> RunShellCommandAsync(
        string command,
        IReadOnlyDictionary<string, string> environment,
        CancellationToken cancellationToken)
    {
        // cmd.exe /c "<complex command>" has notoriously broken quoting rules:
        // embedded `|`, `&`, `<`, `>`, escaped quotes and similar shell-special
        // characters routinely cause cmd to mis-parse the command line. The
        // robust pattern (also used by cloudbase-init) is to write the command
        // verbatim to a temporary .cmd file and run cmd.exe /c on the FILE —
        // there's no quoting at the parent level, so nothing can be mangled.
        //
        // `chcp 65001 >nul` makes cmd.exe emit UTF-8 so the StandardOutputEncoding
        // we set on the ProcessStartInfo decodes correctly. UTF-8 console support
        // has shipped since Windows 7 SP1 — no fallback is needed for our supported
        // platforms.
        var tempScript = Path.Combine(
            Path.GetTempPath(),
            "egs-runcmd-" + Guid.NewGuid().ToString("N") + ".cmd");
        await File.WriteAllTextAsync(
            tempScript,
            "@echo off\r\nchcp 65001 >nul\r\n" + command + "\r\n",
            cancellationToken).ConfigureAwait(false);

        try
        {
            var psi = CreateUtf8Psi("cmd.exe");
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(tempScript);
            ApplyEnvironment(psi, environment);

            return await RunAsync(psi, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try { File.Delete(tempScript); } catch { /* best-effort cleanup */ }
        }
    }

    public Task<RunCommandResult> RunArgvCommandAsync(
        IReadOnlyList<string> argv,
        CancellationToken cancellationToken) =>
        RunArgvCommandAsync(argv, EmptyEnvironment, cancellationToken);

    public async Task<RunCommandResult> RunArgvCommandAsync(
        IReadOnlyList<string> argv,
        IReadOnlyDictionary<string, string> environment,
        CancellationToken cancellationToken)
    {
        if (argv.Count == 0)
            throw new ArgumentException("argv must contain at least one element.", nameof(argv));

        var psi = CreateUtf8Psi(argv[0]);
        for (var i = 1; i < argv.Count; i++)
            psi.ArgumentList.Add(argv[i]);
        ApplyEnvironment(psi, environment);

        return await RunAsync(psi, cancellationToken).ConfigureAwait(false);
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyEnvironment =
        new Dictionary<string, string>(0);

    // Merges caller-supplied vars onto the inherited parent environment.
    // ProcessStartInfo.Environment is pre-populated with the parent env when
    // we instantiate the PSI, so writes here are an overlay (caller wins
    // on collision — that's the intended override semantics).
    private static void ApplyEnvironment(
        ProcessStartInfo psi,
        IReadOnlyDictionary<string, string> environment)
    {
        if (environment.Count == 0)
            return;
        foreach (var (key, value) in environment)
            psi.Environment[key] = value;
    }

    public Task<IReadOnlyList<NetworkAdapterInfo>> GetNetworkAdaptersAsync(CancellationToken cancellationToken) =>
        Task.Run(() => NetworkAdapterInventory.Enumerate(), cancellationToken);

    public Task EnableDhcpAsync(int interfaceIndex, CancellationToken cancellationToken) =>
        Task.Run(() => CimNetworking.SetDhcp(interfaceIndex, enabled: true), cancellationToken);

    public Task DisableDhcpAsync(int interfaceIndex, CancellationToken cancellationToken) =>
        Task.Run(() => CimNetworking.SetDhcp(interfaceIndex, enabled: false), cancellationToken);

    public Task SetStaticIpv4AddressesAsync(
        int interfaceIndex,
        IReadOnlyList<string> addresses,
        CancellationToken cancellationToken) =>
        Task.Run(() => CimNetworking.SetStaticIpv4Addresses(interfaceIndex, addresses), cancellationToken);

    public Task SetIpv4DefaultGatewayAsync(
        int interfaceIndex,
        string? gateway,
        CancellationToken cancellationToken) =>
        Task.Run(() => CimNetworking.SetIpv4DefaultGateway(interfaceIndex, gateway), cancellationToken);

    public Task SetDnsServersAsync(
        int interfaceIndex,
        IReadOnlyList<string> dnsServers,
        CancellationToken cancellationToken) =>
        Task.Run(() => CimNetworking.SetDnsServers(interfaceIndex, dnsServers), cancellationToken);

    public Task EnableDhcp6Async(int interfaceIndex, CancellationToken cancellationToken) =>
        Task.Run(() => CimNetworking.SetDhcp6(interfaceIndex, enabled: true), cancellationToken);

    public Task DisableDhcp6Async(int interfaceIndex, CancellationToken cancellationToken) =>
        Task.Run(() => CimNetworking.SetDhcp6(interfaceIndex, enabled: false), cancellationToken);

    public Task SetStaticIpv6AddressesAsync(
        int interfaceIndex,
        IReadOnlyList<string> addresses,
        CancellationToken cancellationToken) =>
        Task.Run(() => CimNetworking.SetStaticIpv6Addresses(interfaceIndex, addresses), cancellationToken);

    public Task SetIpv6DefaultGatewayAsync(
        int interfaceIndex,
        string? gateway,
        CancellationToken cancellationToken) =>
        Task.Run(() => CimNetworking.SetIpv6DefaultGateway(interfaceIndex, gateway), cancellationToken);

    public Task SetInterfaceRoutesAsync(
        int interfaceIndex,
        IReadOnlyList<CloudConfig.NetworkRoute> routes,
        CancellationToken cancellationToken) =>
        Task.Run(() => CimNetworking.SetInterfaceRoutes(interfaceIndex, routes), cancellationToken);

    public Task SetDnsSearchSuffixesAsync(
        int interfaceIndex,
        IReadOnlyList<string> searchDomains,
        CancellationToken cancellationToken) =>
        Task.Run(() => CimNetworking.SetDnsSearchSuffixes(interfaceIndex, searchDomains), cancellationToken);

    public Task SetInterfaceMtuAsync(
        int interfaceIndex,
        int mtu,
        CancellationToken cancellationToken) =>
        Task.Run(() => CimNetworking.SetInterfaceMtu(interfaceIndex, mtu), cancellationToken);

    public Task<IReadOnlyList<VolumeExtendResult>> ExtendVolumesAsync(
        IReadOnlySet<char>? driveLetterFilter,
        CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<VolumeExtendResult>>(() =>
        {
            // Refresh disk geometry first — without this a guest that booted
            // after the host enlarged the underlying VHD will see the OLD
            // size (GPT secondary header still at the old end-of-disk).
            CimStorage.UpdateDisks(logger);
            return CimStorage.ExtendPartitions(driveLetterFilter, logger);
        }, cancellationToken);

    public async Task ConfigureNtpClientAsync(
        bool enabled,
        IReadOnlyList<string> peers,
        CancellationToken cancellationToken)
    {
        // Mirror cloudbase-init's NTPClientPlugin: w32time service either
        // Automatic+running (with our manualpeerlist) or Disabled+stopped.
        // We drive the start mode + start/stop via Win32_Service CIM rather
        // than sc.exe — the SCM API doesn't depend on argv-quoting niceties.
        if (!enabled)
        {
            await Task.Run(() =>
            {
                Win32.CimService.StopService("w32time", logger);
                Win32.CimService.ChangeStartMode("w32time", Win32.CimService.StartMode.Disabled, logger);
            }, cancellationToken).ConfigureAwait(false);
            return;
        }

        await Task.Run(() =>
        {
            Win32.CimService.ChangeStartMode("w32time", Win32.CimService.StartMode.Automatic, logger);
            Win32.CimService.StartService("w32time", logger);
        }, cancellationToken).ConfigureAwait(false);

        // SCM-trigger setup so w32time follows network availability.
        // sc.exe is the only documented way to manipulate triggers — the WMI
        // surface for Win32_Service does not expose triggerinfo. We delete
        // any existing triggers first so a re-run produces the canonical
        // start/networkon stop/networkoff pair, matching cbi's flow.
        await RunOrThrowAsync(
            "sc.exe",
            ["triggerinfo", "w32time", "delete"],
            cancellationToken,
            ignoreNonZero: true);
        await RunOrThrowAsync(
            "sc.exe",
            ["triggerinfo", "w32time", "start/networkon", "stop/networkoff"],
            cancellationToken,
            ignoreNonZero: true);

        if (peers.Count > 0)
        {
            // w32tm accepts a space-separated list inside one quoted argument.
            // We pass the joined list as a single argv entry — ProcessStartInfo
            // ArgumentList quotes correctly without us hand-rolling the quoting.
            var manualPeerList = string.Join(' ', peers);
            await RunOrThrowAsync(
                "w32tm.exe",
                [
                    "/config",
                    $"/manualpeerlist:{manualPeerList}",
                    "/syncfromflags:manual",
                    "/update",
                ],
                cancellationToken);
        }
    }

    public Task SetRealTimeClockUtcAsync(bool utc, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            // cbi: winreg.SetValueEx(..., 'RealTimeIsUniversal', winreg.REG_DWORD, 1|0).
            // Microsoft.Win32.Registry lives in System.Runtime — no extra dep needed.
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\TimeZoneInformation",
                writable: true)
                ?? throw new InvalidOperationException(
                    @"HKLM\SYSTEM\CurrentControlSet\Control\TimeZoneInformation is missing.");
            key.SetValue("RealTimeIsUniversal", utc ? 1 : 0, Microsoft.Win32.RegistryValueKind.DWord);
            logger.LogInformation("RealTimeIsUniversal set to {Value}.", utc ? 1 : 0);
        }, cancellationToken);

    public async Task SetTimezoneAsync(string windowsTimezoneId, CancellationToken cancellationToken)
    {
        // tzutil /s "<id>" applies immediately and writes the registry entries
        // (TimeZoneKeyName + dynamic DST data). It's also what the Settings UI
        // uses under the hood, so the result is observably identical to a
        // user-driven change.
        await RunOrThrowAsync("tzutil.exe", ["/s", windowsTimezoneId], cancellationToken);
    }

    public async Task<LocaleApplyResult> ApplyLocaleAsync(LocaleSpec spec, CancellationToken cancellationToken)
    {
        // Set-* cmdlets in PowerShell are the documented way to manipulate
        // locale / UI language / keyboard layout on Windows Server. Their
        // COM-based underpinnings are awkward to call directly from .NET, so
        // we drive them via powershell.exe with a tightly-scoped script.
        var script = BuildLocaleScript(spec);
        var psi = CreateUtf8Psi("powershell.exe");
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(script);

        var result = await RunAsync(psi, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"powershell.exe applying locale failed (exit {result.ExitCode}): {result.StdErr.Trim()}");
        }

        // The script's final line is "REBOOT_REQUIRED=true" or =false.
        var rebootRequired = result.StdOut
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Any(line => line.Trim().Equals("REBOOT_REQUIRED=true", StringComparison.OrdinalIgnoreCase));
        return new LocaleApplyResult { RebootRequired = rebootRequired };
    }

    public async Task ApplyLicenseAsync(LicenseSpec spec, CancellationToken cancellationToken)
    {
        // slmgr.vbs is the canonical Windows licensing interface. //b suppresses
        // GUI popups; //nologo trims the cscript banner. We use cscript over
        // wscript so output goes to stdout/stderr (wscript would pop a dialog
        // for `/dlv` style queries — we never call those, but cscript is the
        // safe default).
        var slmgrPath = SlmgrPath();

        if (!string.IsNullOrWhiteSpace(spec.ProductKey))
        {
            await RunOrThrowAsync(
                "cscript.exe",
                ["//nologo", "//b", slmgrPath, "/ipk", spec.ProductKey],
                cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(spec.KmsHost))
        {
            await RunOrThrowAsync(
                "cscript.exe",
                ["//nologo", "//b", slmgrPath, "/skms", spec.KmsHost],
                cancellationToken);
        }
        else if (spec.ClearKmsHost)
        {
            // /ckms clears the configured host so DNS SRV discovery takes
            // over — the canonical "go back to auto" lever.
            await RunOrThrowAsync(
                "cscript.exe",
                ["//nologo", "//b", slmgrPath, "/ckms"],
                cancellationToken);
        }

        if (spec.Activate)
        {
            await RunOrThrowAsync(
                "cscript.exe",
                ["//nologo", "//b", slmgrPath, "/ato"],
                cancellationToken);
        }
    }

    public async Task<RearmResult> RearmLicenseAsync(CancellationToken cancellationToken)
    {
        await RunOrThrowAsync(
            "cscript.exe",
            ["//nologo", "//b", SlmgrPath(), "/rearm"],
            cancellationToken);
        // Per Microsoft docs (and confirmed in the eryph rearm-evaluation.ps1
        // gene), the rearm count is reset/decremented in the registry but the
        // grace period only resets after reboot. Always advertise the reboot.
        return new RearmResult { RebootRequired = true };
    }

    public Task<string?> ResolveVolumeActivationKeyAsync(
        VolumeActivationKeyType type,
        CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            var product = Licensing.CimLicensing.FindActiveKmsClientProduct();
            if (product is null)
            {
                logger.LogWarning(
                    "Could not find an active KMS-client product in SoftwareLicensingProduct; cannot auto-resolve volume activation key.");
                return (string?)null;
            }

            var osFamily = Licensing.OsVersionDetector.Detect();
            if (osFamily == Licensing.OsVersionFamily.Unknown)
            {
                logger.LogWarning(
                    "Unrecognised OS version {Version}; cannot resolve volume activation key.",
                    Environment.OSVersion.Version);
                return null;
            }

            var keyType = type == VolumeActivationKeyType.Avma
                ? Licensing.VolumeActivationType.Avma
                : Licensing.VolumeActivationType.Kms;

            var key = Licensing.VolumeActivationKeys.Lookup(osFamily, product.LicenseFamily, keyType);
            if (key is null)
            {
                logger.LogWarning(
                    "No {Type} key found for OS {OsFamily} + LicenseFamily {Family}.",
                    type, osFamily, product.LicenseFamily);
            }
            return key;
        }, cancellationToken);

    public Task<bool> IsEvaluationLicenseAsync(CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            var product = Licensing.CimLicensing.FindActiveKmsClientProduct();
            return product?.IsEvaluation ?? false;
        }, cancellationToken);

    public async Task RequestPowerStateAsync(PowerStateRequest request, CancellationToken cancellationToken)
    {
        // shutdown.exe action flag. /h (hibernate) is the closest Windows
        // analogue to cloud-init's `halt` — neither "stop CPU, leave power
        // on" semantics exist on Windows. The PowerStateModule logs a
        // Warning so operators know we substituted.
        var actionFlag = request.Action switch
        {
            PowerStateAction.Reboot => "/r",
            PowerStateAction.Poweroff => "/s",
            PowerStateAction.Halt => "/h",
            _ => throw new ArgumentOutOfRangeException(nameof(request), request.Action, "Unknown power-state action."),
        };

        var argv = new List<string>
        {
            actionFlag,
            "/f",                                  // force-close apps (we're unattended)
            "/t", request.DelaySeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

        if (!string.IsNullOrEmpty(request.Message))
        {
            // shutdown.exe truncates /c silently after 512 chars; clamp on
            // our side so log messages are honest about what was sent.
            var msg = request.Message.Length > 512 ? request.Message[..512] : request.Message;
            argv.Add("/c");
            argv.Add(msg);
        }

        // shutdown.exe with /r doesn't actually return until either the
        // delay expires or `shutdown /a` aborts it — UNLESS /t > 0, in
        // which case it schedules and returns immediately. We always use
        // /t (>= 0) so the call returns promptly and the StageRunner can
        // finish writing its semaphores before Windows actually goes down.
        await RunOrThrowAsync("shutdown.exe", argv, cancellationToken);
        logger.LogInformation(
            "Scheduled {Action} in {Delay}s via shutdown.exe (message='{Msg}').",
            request.Action, request.DelaySeconds, request.Message ?? "<none>");
    }

    // ---- OpenSSH daemon (Win32-OpenSSH at C:\ProgramData\ssh) — RFC 0018 ----

    private const string SshServerCapability = "OpenSSH.Server~~~~0.0.1.0";

    private static readonly IReadOnlySet<string> SupportedHostKeyTypes =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ed25519", "ecdsa", "rsa" };

    private static string SshDir() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ssh");

    public Task<bool> IsSshdInstalledAsync(CancellationToken cancellationToken) =>
        // sshd is "installed" when its Win32_Service entry exists, regardless
        // of run state. CimService.GetState returns null when not installed.
        Task.Run(() => CimService.GetState("sshd") is not null, cancellationToken);

    public async Task InstallOpenSshServerAsync(CancellationToken cancellationToken)
    {
        if (await IsSshdInstalledAsync(cancellationToken).ConfigureAwait(false))
        {
            logger.LogDebug("sshd already installed; skipping OpenSSH server capability install.");
            return;
        }

        // Add-WindowsCapability is the documented install path for the inbox
        // OpenSSH server. We drive it via powershell.exe (DISM's capability
        // surface is awkward from managed code) and force UTF-8 on the streams.
        var script =
            "[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new()\n"
            + "$ErrorActionPreference = 'Stop'\n"
            + $"Add-WindowsCapability -Online -Name '{SshServerCapability}' | Out-Null\n";
        var psi = CreateUtf8Psi("powershell.exe");
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(script);

        var result = await RunAsync(psi, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new InvalidOperationException(
                $"Add-WindowsCapability {SshServerCapability} failed (exit {result.ExitCode}): {result.StdErr.Trim()}");

        // Set sshd to start automatically so the server survives reboots.
        await Task.Run(
            () => CimService.ChangeStartMode("sshd", CimService.StartMode.Automatic, logger),
            cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Installed OpenSSH server capability and set sshd to start Automatically.");
    }

    public async Task<IReadOnlyList<SshHostKeyFingerprint>> RegenerateSshHostKeysAsync(
        IReadOnlyList<string> keyTypes,
        bool deleteExisting,
        CancellationToken cancellationToken)
    {
        var sshDir = SshDir();
        Directory.CreateDirectory(sshDir);

        if (deleteExisting)
        {
            foreach (var file in Directory.EnumerateFiles(sshDir, "ssh_host_*_key")
                         .Concat(Directory.EnumerateFiles(sshDir, "ssh_host_*_key.pub")))
            {
                try { File.Delete(file); }
                catch (Exception ex) { logger.LogWarning(ex, "Failed to delete existing host key {File}.", file); }
            }
        }

        var keygen = Path.Combine(
            Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows",
            "System32", "OpenSSH", "ssh-keygen.exe");

        var fingerprints = new List<SshHostKeyFingerprint>();
        foreach (var rawType in keyTypes)
        {
            var type = rawType.Trim().ToLowerInvariant();
            if (!SupportedHostKeyTypes.Contains(type))
            {
                // DSA was removed in OpenSSH 9.8; unknown types are likewise skipped.
                logger.LogWarning("Skipping unsupported ssh host-key type '{Type}' (allowed: ed25519, ecdsa, rsa).", rawType);
                continue;
            }

            var keyPath = Path.Combine(sshDir, $"ssh_host_{type}_key");
            // ssh-keygen refuses to overwrite an existing key non-interactively,
            // so remove any leftover when we're not in delete-everything mode.
            try { File.Delete(keyPath); } catch { /* best effort */ }
            try { File.Delete(keyPath + ".pub"); } catch { /* best effort */ }

            var argv = new List<string> { "-t", type, "-f", keyPath, "-N", "", "-q" };
            if (type == "rsa")
            {
                // RSA-3072 floor — matches modern OpenSSH defaults and our policy.
                argv.Add("-b");
                argv.Add("3072");
            }

            await RunOrThrowAsync(keygen, argv, cancellationToken).ConfigureAwait(false);
            ApplySshHostKeyAcl(keyPath);

            fingerprints.Add(await ReadHostKeyFingerprintAsync(keygen, type, keyPath, cancellationToken)
                .ConfigureAwait(false));
        }

        return fingerprints;
    }

    public Task WriteSshHostKeyAsync(
        string keyType,
        string privatePem,
        string? publicLine,
        CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            var sshDir = SshDir();
            Directory.CreateDirectory(sshDir);

            var keyPath = Path.Combine(sshDir, $"ssh_host_{keyType.Trim().ToLowerInvariant()}_key");
            var utf8 = new UTF8Encoding(false);
            // OpenSSH PEM keys are LF-terminated; normalize so a CRLF-carrying
            // operator value doesn't confuse ssh-keygen.
            File.WriteAllBytes(keyPath, utf8.GetBytes(NormalizeLf(privatePem)));
            ApplySshHostKeyAcl(keyPath);

            if (!string.IsNullOrWhiteSpace(publicLine))
                File.WriteAllBytes(keyPath + ".pub", utf8.GetBytes(NormalizeLf(publicLine!.TrimEnd()) + "\n"));
        }, cancellationToken);

    public Task EnsureSshdConfigIncludeAsync(CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            var sshDir = SshDir();
            Directory.CreateDirectory(sshDir);
            Directory.CreateDirectory(Path.Combine(sshDir, "sshd_config.d"));

            var configPath = Path.Combine(sshDir, "sshd_config");

            var existing = File.Exists(configPath)
                ? File.ReadAllText(configPath, new UTF8Encoding(false))
                : "";

            var updated = EnsureSshdConfigInclude(existing);
            if (updated is null)
                return; // Include already present — idempotent no-op.

            File.WriteAllBytes(configPath, new UTF8Encoding(false).GetBytes(updated));
            logger.LogInformation("Prepended Include for sshd_config.d to sshd_config.");
        }, cancellationToken);

    public Task WriteSshdDropInAsync(
        string dropInFileName,
        string contents,
        CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            var dropInDir = Path.Combine(SshDir(), "sshd_config.d");
            Directory.CreateDirectory(dropInDir);
            var path = Path.Combine(dropInDir, dropInFileName);
            var content = NormalizeLf(contents);
            if (content.Length > 0 && !content.EndsWith('\n'))
                content += "\n";
            File.WriteAllBytes(path, new UTF8Encoding(false).GetBytes(content));
        }, cancellationToken);

    public Task RestartSshdAsync(CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            if (CimService.GetState("sshd") is null)
            {
                logger.LogWarning("sshd is not installed; skipping restart.");
                return;
            }

            CimService.StopService("sshd", logger);
            CimService.StartService("sshd", logger);
            logger.LogInformation("Restarted sshd to reload configuration.");
        }, cancellationToken);

    public Task<string> ResolveBuiltinAdministratorNameAsync(CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            // The built-in Administrator is RID 500. Its name can be changed by
            // policy, so we resolve by SID rather than assuming "Administrator".
            // We enumerate the local Administrators group members, translate each
            // to a SID, and return the one whose SID ends with "-500".
            try
            {
                var members = NetUserHelpers.GetGroupMemberNames(WellKnownGroups.AdministratorsName());
                foreach (var member in members)
                {
                    try
                    {
                        var sid = (SecurityIdentifier)new NTAccount(member).Translate(typeof(SecurityIdentifier));
                        if (sid.Value.EndsWith("-500", StringComparison.Ordinal))
                        {
                            // Return the bare account name (strip any DOMAIN\ prefix).
                            var slash = member.IndexOf('\\');
                            return slash >= 0 ? member[(slash + 1)..] : member;
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogDebug(ex, "Could not translate '{Member}' to a SID while resolving RID-500.", member);
                    }
                }
            }
            catch (Exception ex)
            {
                // Documented failure branch (memory: feedback_pinvoke_error_branches):
                // NetLocalGroupGetMembers / SID translation can fail on a
                // domain-joined or unusually-configured box. Fall back to the
                // literal name so disable_root still produces a usable directive.
                logger.LogWarning(ex,
                    "Failed to enumerate Administrators for RID-500 resolution; falling back to 'Administrator'.");
            }

            logger.LogWarning("Could not resolve the RID-500 account by SID; falling back to 'Administrator'.");
            return "Administrator";
        }, cancellationToken);

    private async Task<SshHostKeyFingerprint> ReadHostKeyFingerprintAsync(
        string keygen,
        string keyType,
        string keyPath,
        CancellationToken cancellationToken)
    {
        var pubPath = keyPath + ".pub";
        var publicLine = File.Exists(pubPath)
            ? (await File.ReadAllTextAsync(pubPath, cancellationToken).ConfigureAwait(false)).Trim()
            : "";

        // ssh-keygen -l -f <pub> prints "<bits> SHA256:... <comment> (<TYPE>)".
        var psi = CreateUtf8Psi(keygen);
        psi.ArgumentList.Add("-l");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add(pubPath);
        var result = await RunAsync(psi, cancellationToken).ConfigureAwait(false);

        var fingerprint = result.StdOut
            .Split([' ', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(t => t.StartsWith("SHA256:", StringComparison.Ordinal))
            ?? result.StdOut.Trim();

        return new SshHostKeyFingerprint(keyType, fingerprint, publicLine);
    }

    private static string NormalizeLf(string value) =>
        value.Replace("\r\n", "\n").Replace("\r", "\n");

    // internal for unit testing — pure form of the EnsureSshdConfigInclude
    // logic. Returns the new file content, or null when the Include is already
    // present (idempotent no-op). The Include is prepended so its
    // first-obtained-value settings win over shipped directives and the
    // trailing `Match Group administrators` block.
    internal static string? EnsureSshdConfigInclude(string existing)
    {
        const string includeDirective = "Include sshd_config.d/*.conf";

        var hasInclude = existing
            .Split('\n')
            .Select(l => l.Trim())
            .Any(l => !l.StartsWith('#')
                      && l.StartsWith("Include", StringComparison.OrdinalIgnoreCase)
                      && l.Contains("sshd_config.d", StringComparison.OrdinalIgnoreCase));
        if (hasInclude)
            return null;

        var normalized = NormalizeLf(existing);
        return includeDirective + "\n"
            + (normalized.Length == 0 ? "" : normalized + (normalized.EndsWith('\n') ? "" : "\n"));
    }

    // Host-key ACL: owner SYSTEM, SYSTEM + Administrators FullControl, nothing
    // else (replicates FixHostFilePermissions.ps1 without shelling out). sshd
    // refuses to start if a private host key is readable by anyone else.
    private static void ApplySshHostKeyAcl(string path)
    {
        var info = new FileInfo(path);
        var security = info.GetAccessControl();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, false, typeof(SecurityIdentifier)))
            security.RemoveAccessRuleSpecific(rule);

        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        security.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(admins, FileSystemRights.FullControl, AccessControlType.Allow));
        security.SetOwner(system);

        info.SetAccessControl(security);
    }

    private static string SlmgrPath()
    {
        var systemRoot = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
        return Path.Combine(systemRoot, "System32", "slmgr.vbs");
    }

    // RunOrThrowAsync is invoked for a handful of native tools (tzutil.exe,
    // w32tm.exe, slmgr.vbs via cscript.exe, shutdown.exe, sc.exe). Those tools
    // emit their (often localized) output in the OEM code page. We still set
    // StandardOutputEncoding = UTF-8 here for consistency with the rest of the
    // codebase: only exit codes and short status strings drive control flow,
    // and the rare mojibake-on-error in a localized stderr is preferable to a
    // per-tool encoding matrix. If we ever need accurate stderr for one of
    // these tools, switch *that* spawn site to Console.OutputEncoding.
    private async Task RunOrThrowAsync(
        string fileName,
        IReadOnlyList<string> argv,
        CancellationToken cancellationToken,
        bool ignoreNonZero = false)
    {
        var psi = CreateUtf8Psi(fileName);
        foreach (var a in argv) psi.ArgumentList.Add(a);
        var result = await RunAsync(psi, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0 && !ignoreNonZero)
        {
            throw new InvalidOperationException(
                $"{fileName} {string.Join(' ', argv)} exited {result.ExitCode}: {result.StdErr.Trim()}");
        }
    }

    // Common ProcessStartInfo factory: redirected stdout/stderr, no window,
    // UTF-8 decoding on both streams. Callers that spawn PowerShell or cmd.exe
    // must ALSO ensure the child writes UTF-8 (chcp 65001 / [Console]::OutputEncoding);
    // this helper only governs how .NET decodes what the child emits.
    private static ProcessStartInfo CreateUtf8Psi(string fileName) =>
        new(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

    // internal for unit testing — the PowerShell-string-building logic is a
    // non-trivial composition root that we want exercised directly.
    internal static string BuildLocaleScript(LocaleSpec spec)
    {
        // The script is intentionally compact: it queries current state, sets
        // only what's different, and emits a single `REBOOT_REQUIRED=...` line
        // for the caller to parse. Any cmdlet failure terminates via -ErrorAction
        // Stop so RunAsync sees a non-zero exit.
        var sb = new StringBuilder();
        // Force UTF-8 on every stream so the stdout we parse (REBOOT_REQUIRED=...)
        // and any error text we surface are decoded correctly by .NET's
        // StandardOutputEncoding=UTF8. Without this, PowerShell 5.1 emits in
        // the OEM code page and the .NET-side UTF-8 decoder mangles non-ASCII.
        sb.AppendLine("[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new()");
        sb.AppendLine("$OutputEncoding = [System.Text.UTF8Encoding]::new()");
        sb.AppendLine("[Console]::InputEncoding = [System.Text.UTF8Encoding]::new()");
        sb.AppendLine("$ErrorActionPreference = 'Stop'");
        sb.AppendLine("$rebootRequired = $false");

        if (!string.IsNullOrWhiteSpace(spec.Locale))
        {
            var locale = EscapePsSingleQuoted(spec.Locale);
            // Set-Culture / Set-WinUILanguageOverride / Set-WinUserLanguageList
            // are all sign-out-or-cmdlet-session safe. Set-WinSystemLocale needs
            // a reboot to fully take effect, which we surface via the marker line.
            sb.AppendLine($"Set-Culture -CultureInfo '{locale}'");
            sb.AppendLine($"Set-WinUILanguageOverride -Language '{locale}'");
            sb.AppendLine($"$langList = New-WinUserLanguageList -Language '{locale}'");
            if (!string.IsNullOrWhiteSpace(spec.KeyboardLayout))
            {
                var kb = EscapePsSingleQuoted(spec.KeyboardLayout);
                sb.AppendLine("$langList[0].InputMethodTips.Clear()");
                sb.AppendLine($"$langList[0].InputMethodTips.Add('{kb}')");
            }
            sb.AppendLine("Set-WinUserLanguageList -LanguageList $langList -Force");
            sb.AppendLine($"$currentSys = (Get-WinSystemLocale).Name");
            sb.AppendLine($"if ($currentSys -ne '{locale}') {{");
            sb.AppendLine($"  Set-WinSystemLocale -SystemLocale '{locale}'");
            sb.AppendLine($"  $rebootRequired = $true");
            sb.AppendLine("}");
        }
        else if (!string.IsNullOrWhiteSpace(spec.KeyboardLayout))
        {
            // Keyboard-only change: amend the existing language list rather
            // than replacing it — the user may already have multiple langs.
            var kb = EscapePsSingleQuoted(spec.KeyboardLayout);
            sb.AppendLine("$langList = Get-WinUserLanguageList");
            sb.AppendLine($"$langList[0].InputMethodTips.Clear()");
            sb.AppendLine($"$langList[0].InputMethodTips.Add('{kb}')");
            sb.AppendLine("Set-WinUserLanguageList -LanguageList $langList -Force");
        }

        sb.AppendLine("Write-Output \"REBOOT_REQUIRED=$rebootRequired\"");
        return sb.ToString();
    }

    private static string EscapePsSingleQuoted(string value) => PowerShellScriptWrapper.EscapeSingleQuoted(value);

    public string TranslateUnixPath(string unixPath)
    {
        if (string.IsNullOrWhiteSpace(unixPath))
            throw new ArgumentException("Path is empty.", nameof(unixPath));

        // Reject ".." segments outright — user-controlled paths must not be
        // able to escape their intended mapping via traversal. We check on the
        // raw input (before splitting) so both "/foo/../bar" and "C:\foo\..\bar"
        // are rejected.
        if (ContainsParentSegment(unixPath))
            throw new ArgumentException(
                $"Path contains '..' segment, which is not allowed: '{unixPath}'.", nameof(unixPath));

        string candidate;

        // Pass through if the path is already drive-rooted or UNC.
        if (unixPath.Length >= 2 && unixPath[1] == ':')
        {
            candidate = unixPath.Replace('/', '\\');
        }
        else if (unixPath.StartsWith(@"\\", StringComparison.Ordinal))
        {
            // UNC paths are returned as-is and are not anchored under C:\.
            // We still canonicalize to flush any stray separators or dot
            // segments that slipped past the early check.
            return WindowsPath.GetFullPath(unixPath);
        }
        else if (!unixPath.StartsWith('/'))
        {
            throw new ArgumentException(
                $"Expected an absolute unix path or a drive-rooted Windows path, got '{unixPath}'.", nameof(unixPath));
        }
        else
        {
            var segments = unixPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0)
                return "C:\\";

            candidate = segments[0] switch
            {
                "home" when segments.Length >= 2 =>
                    WindowsPath.Combine(["C:\\Users", .. segments[1..]]),
                "root" =>
                    WindowsPath.Combine(["C:\\Users\\Administrator", .. segments[1..]]),
                "tmp" =>
                    WindowsPath.Combine([Path.GetTempPath().TrimEnd('\\'), .. segments[1..]]),
                "var" =>
                    WindowsPath.Combine(
                        [Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), .. segments[1..]]),
                _ =>
                    WindowsPath.Combine(["C:\\", .. segments]),
            };
        }

        // Canonicalize and verify the result still anchors under C:\. The
        // mapping documented at the top of this file lands everything below
        // C:\ (including /tmp -> %TEMP% and /var -> %ProgramData%, both of
        // which are C:\ subtrees on a default Windows install). Anything that
        // canonicalizes elsewhere is rejected.
        var full = WindowsPath.GetFullPath(candidate);
        if (!full.StartsWith(@"C:\", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                $"Translated path '{full}' is outside the allowed C:\\ root (input was '{unixPath}').",
                nameof(unixPath));

        return full;
    }

    private static bool ContainsParentSegment(string path)
    {
        foreach (var segment in path.Split(['/', '\\']))
        {
            if (segment == "..")
                return true;
        }
        return false;
    }

    public string ExpandEnvironmentVariables(string value) =>
        Environment.ExpandEnvironmentVariables(value);

    public char? GetSystemDriveLetter()
    {
        // %SystemDrive% is "C:" on every supported Windows install but we
        // still resolve it dynamically so reimaged guests with a non-C:
        // system drive work correctly.
        var sysDrive = Environment.GetEnvironmentVariable("SystemDrive");
        if (string.IsNullOrWhiteSpace(sysDrive)) return null;
        var ch = sysDrive[0];
        if (ch is >= 'a' and <= 'z') ch = char.ToUpperInvariant(ch);
        return ch is >= 'A' and <= 'Z' ? ch : null;
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
