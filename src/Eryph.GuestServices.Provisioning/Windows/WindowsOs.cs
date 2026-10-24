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
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Eryph.GuestServices.Core;
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
            // /d skips any AutoRun command configured in the Command Processor
            // registry key, and /v:off forces delayed expansion OFF regardless of
            // the guest's global setting, so a '!' in the command stays literal
            // (cmd's default) instead of triggering !var! expansion. Matches the
            // flags BuildCmdBatchArguments uses for the batch-dispatch path.
            psi.ArgumentList.Add("/d");
            psi.ArgumentList.Add("/v:off");
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(tempScript);
            ApplyRefreshedPath(psi);
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

        // CreateProcess resolves a bare argv[0] against the PARENT's PATH, not
        // the (refreshed) environment block we hand the child — so a tool
        // installed this boot would not be found by name in an argv-form runcmd.
        // Resolve it ourselves against the refreshed PATH first.
        var resolved = ResolveExecutableOnRefreshedPath(argv[0]);

        ProcessStartInfo psi;
        if (IsBatchFile(resolved))
        {
            // A just-installed tool often resolves to a .cmd/.bat shim (e.g.
            // npm.cmd). CreateProcess (UseShellExecute=false) cannot launch a
            // batch file directly, so dispatch it through cmd.exe. cmd re-parses
            // its command line with DIFFERENT rules than the MSVCRT quoting that
            // ArgumentList produces: an unquoted metacharacter (& | < > ^) would
            // split or redirect the line, and cmd's default /c outer-quote
            // heuristic can strip the quotes around a space-containing path. So
            // hand-build the line with cmd-correct quoting and pass it verbatim.
            psi = CreateUtf8Psi("cmd.exe");
            psi.Arguments = BuildCmdBatchArguments(resolved, argv);
        }
        else
        {
            psi = CreateUtf8Psi(resolved);
            for (var i = 1; i < argv.Count; i++)
                psi.ArgumentList.Add(argv[i]);
        }
        ApplyRefreshedPath(psi);
        ApplyEnvironment(psi, environment);

        return await RunAsync(psi, cancellationToken).ConfigureAwait(false);
    }

    private static bool IsBatchFile(string path) =>
        path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);

    // Builds the raw command line for launching a batch file through cmd.exe,
    // to be assigned verbatim to ProcessStartInfo.Arguments (NOT ArgumentList,
    // whose MSVCRT quoting cmd would re-parse incorrectly):
    //
    //   /d /v:off /s /c "<batch> <arg> <arg> ..."
    //
    // /d skips AutoRun, /v:off forces delayed expansion OFF so a '!' in an
    // argument stays literal even when the guest enabled delayed expansion
    // globally (otherwise "a!PATH!b" would expand inside the quotes), and /s
    // makes cmd strip exactly the first and last quote of the remainder
    // (deterministic — no reliance on the fragile outer-quote heuristic).
    // `chcp 65001>nul &` sets the cmd session to UTF-8 before the
    // batch runs so its output (e.g. `echo %~1` with non-ASCII) is decoded by
    // our StandardOutputEncoding=UTF8 — the batch is run directly, so unlike
    // RunShellCommandAsync we cannot inject chcp into the script itself. The
    // trailing exit code stays the batch's (`&`, not `&&`). Every token is
    // wrapped in one pair of double quotes, so cmd treats embedded
    // metacharacters (& | < > ^ ( )) as literal. cmd does NOT honour backslash
    // as a quote escape and expands variables / breaks lines even inside quotes,
    // so an argument containing a double quote, '%', CR or LF cannot be carried
    // safely (an embedded quote would close the pair early and expose the rest
    // as live cmd syntax — command injection). Such an argument is rejected
    // rather than corrupted or mis-executed; everything else is emitted literally.
    internal static string BuildCmdBatchArguments(string batchPath, IReadOnlyList<string> argv)
    {
        var sb = new StringBuilder("/d /v:off /s /c \"chcp 65001>nul & ");
        AppendCmdQuotedArgument(sb, batchPath);
        for (var i = 1; i < argv.Count; i++)
        {
            sb.Append(' ');
            AppendCmdQuotedArgument(sb, argv[i]);
        }
        sb.Append('"');
        return sb.ToString();
    }

    private static void AppendCmdQuotedArgument(StringBuilder sb, string argument)
    {
        if (argument.AsSpan().IndexOfAny("\"%\r\n") >= 0)
            throw new ArgumentException(
                "A batch-file argument may not contain a double quote, '%', CR or LF — "
                + $"cmd.exe cannot safely escape these on a command line. Argument: '{argument}'.",
                nameof(argument));

        // One pair of double quotes, content kept literal. cmd leaves the
        // metacharacters inside the quotes alone; backslashes pass through
        // unchanged (cmd does not treat them as escapes, and we reject the only
        // character — a double quote — that would make a trailing backslash
        // ambiguous).
        sb.Append('"');
        sb.Append(argument);
        sb.Append('"');
    }

    // Resolves a bare executable name (no directory component) against the
    // CURRENT machine + user PATH and PATHEXT. Returns the original string when
    // already rooted or not found (letting the OS do its default resolution).
    private static string ResolveExecutableOnRefreshedPath(string exe)
    {
        try
        {
            if (Path.IsPathRooted(exe)
                || exe.Contains(Path.DirectorySeparatorChar)
                || exe.Contains(Path.AltDirectorySeparatorChar))
                return exe;

            var machine = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.Machine) ?? "";
            var user = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User) ?? "";
            var dirs = (machine + ";" + user)
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var pathext = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var nameExt = Path.GetExtension(exe);
            // Only treat the name as already carrying its executable extension when
            // that extension is itself a PATHEXT entry. A name like "python3.11"
            // has extension ".11" (not executable), so we must still probe
            // "python3.11" + each PATHEXT ("python3.11.exe"), not only the literal.
            var exts = nameExt.Length > 0 && pathext.Any(p => p.Equals(nameExt, StringComparison.OrdinalIgnoreCase))
                ? [""]
                : (string[])[.. pathext.Prepend("")];

            foreach (var dir in dirs)
            {
                foreach (var ext in exts)
                {
                    var candidate = Path.Combine(dir, exe + ext);
                    if (File.Exists(candidate))
                        return candidate;
                }
            }
        }
        catch
        {
            // Fall back to the original name (non-Windows host / odd PATH).
        }

        return exe;
    }

    // Overrides the child's PATH with the CURRENT machine + user PATH read from
    // the registry. The agent process inherited its PATH at service start, so a
    // tool installed this boot (winget/chocolatey adding to the machine PATH)
    // would otherwise be invisible to a same-boot runcmd / user script until a
    // reboot. Caller-supplied environment overlays this (ApplyEnvironment runs
    // after), so an explicit PATH override still wins. Best-effort: on any
    // failure the inherited PATH is kept.
    private static void ApplyRefreshedPath(ProcessStartInfo psi)
    {
        try
        {
            var machine = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.Machine);
            var user = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User);
            var combined = string.Join(
                ';', new[] { machine, user }.Where(p => !string.IsNullOrEmpty(p)));
            if (!string.IsNullOrEmpty(combined))
                psi.Environment["Path"] = combined;
        }
        catch
        {
            // Keep the inherited PATH (e.g. non-Windows host in a unit test).
        }
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

    public async Task RenameNetworkAdapterAsync(
        int interfaceIndex,
        string newName,
        CancellationToken cancellationToken)
    {
        // Renaming is done via the NetAdapter cmdlet rather than raw CIM: the
        // MSFT_NetAdapter "Name" is the NetConnectionID, and the cmdlet keys the
        // adapter by interface index (stable across the rename) and applies the
        // change through the same parameterised envelope PowerShell uses. This
        // mirrors the DNS path, which is cmdlet-driven for the same reason.
        await RunOrThrowAsync(
            "powershell.exe",
            ["-NoProfile", "-NonInteractive", "-Command", BuildRenameNetAdapterCommand(interfaceIndex, newName)],
            cancellationToken).ConfigureAwait(false);
    }

    // internal for unit testing — the -Command string is a composition root we
    // want exercised directly (a wrong cmdlet/parameter name or a quoting slip
    // would only surface on a real guest). Pipe Get-NetAdapter (keyed by the
    // stable interface index) into Rename-NetAdapter so the current connection
    // name never has to be quoted into the command.
    internal static string BuildRenameNetAdapterCommand(int interfaceIndex, string newName) =>
        $"Get-NetAdapter -InterfaceIndex {interfaceIndex} | "
        + $"Rename-NetAdapter -NewName '{EscapePsSingleQuoted(newName)}' -ErrorAction Stop";

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

    public async Task SetDnsServersAsync(
        int interfaceIndex,
        IReadOnlyList<string> dnsServers,
        CancellationToken cancellationToken)
    {
        // DNS servers are set via the Set-DnsClientServerAddress cmdlet, not CIM.
        // MSFT_DNSClientServerAddress exposes no settable property (ServerAddresses
        // is ReadOnly) and no static set method (only RequestStateChange); the
        // cmdlet sets DNS through a parameterized ModifyInstance envelope that a
        // raw MI ModifyInstance cannot reproduce — the DCOM provider rejects the
        // custom options with "Invalid parameter". The cmdlet takes the interface
        // index directly and assigns a mixed IPv4+IPv6 list to the correct family,
        // so the whole list goes in one call. An empty list resets the interface
        // back to DHCP-provided servers.
        await RunOrThrowAsync(
            "powershell.exe",
            ["-NoProfile", "-NonInteractive", "-Command", BuildSetDnsServersCommand(interfaceIndex, dnsServers)],
            cancellationToken).ConfigureAwait(false);
    }

    // internal for unit testing — the -Command string is a composition root we
    // want exercised directly (a wrong cmdlet/parameter name or a quoting slip
    // would otherwise only surface on a real guest). Null/blank entries are
    // dropped first: the YAML 1.1 resolver turns a `~`/`null`/empty nameserver
    // into a null/blank string, which would NRE in FormatPsStringList or emit an
    // invalid -ServerAddresses. A list that is empty (or empties out after that)
    // resets the interface back to DHCP-provided servers; otherwise the whole
    // (possibly mixed IPv4+IPv6) list is handed to the cmdlet, which assigns each
    // address to the correct family.
    internal static string BuildSetDnsServersCommand(int interfaceIndex, IReadOnlyList<string> dnsServers)
    {
        var servers = dnsServers
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .ToList();

        return servers.Count == 0
            ? $"Set-DnsClientServerAddress -InterfaceIndex {interfaceIndex} -ResetServerAddresses -ErrorAction Stop"
            : $"Set-DnsClientServerAddress -InterfaceIndex {interfaceIndex} "
              + $"-ServerAddresses {FormatPsStringList(servers)} -ErrorAction Stop";
    }

    // Render a PowerShell single-quoted, comma-separated string array literal
    // (e.g. 'a','b') for use inside a -Command script. Each value is escaped so
    // a stray single quote can't break out of the literal.
    private static string FormatPsStringList(IReadOnlyList<string> values) =>
        string.Join(",", values.Select(v => $"'{EscapePsSingleQuoted(v)}'"));

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

    public Task SetServiceControlFlagAsync(
        ServiceControlFlag flag,
        bool enabled,
        CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            // Same key + value names the service reads at start
            // (PlatformServiceControlFlags). CreateSubKey opens-or-creates so
            // the very first flag write on a stock image works without a
            // pre-seeded key.
            var valueName = PlatformServiceControlFlags.GetValueName(flag);
            using var key = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(
                PlatformServiceControlFlags.WindowsServiceControlKey, writable: true)
                ?? throw new InvalidOperationException(
                    $@"Could not open/create HKLM\{PlatformServiceControlFlags.WindowsServiceControlKey}.");
            key.SetValue(valueName, enabled ? 1 : 0, Microsoft.Win32.RegistryValueKind.DWord);
            logger.LogInformation(
                "Service-control flag {Flag} set to {Value} (effective on next service start).",
                valueName, enabled ? 1 : 0);
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

    // OS build 17763 = Windows 10 1809 / Windows Server 2019 — the first release
    // where the OpenSSH.Server Feature-on-Demand is a supported inbox capability.
    private const int OpenSshServerMinBuild = 17763;

    public Task<bool> IsOpenSshServerCapabilityAvailableAsync(CancellationToken cancellationToken) =>
        Task.FromResult(IsOpenSshServerCapabilityAvailable(Environment.OSVersion.Version));

    // internal + pure for unit testing. .NET 5+ reports the real OS build (no
    // manifest clamping — see OsVersionDetector), so a build check is reliable.
    internal static bool IsOpenSshServerCapabilityAvailable(Version osVersion) =>
        osVersion.Major > 10
        || (osVersion.Major == 10 && osVersion.Build >= OpenSshServerMinBuild);

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

    public Task EnsureSshdAutostartAsync(CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            // Defensive: the caller gates on "installed", but a service that
            // vanished between the check and here must not throw a "not found".
            if (CimService.GetState("sshd") is null)
            {
                logger.LogWarning("sshd is not installed; cannot enable autostart.");
                return;
            }

            // Both are idempotent (ChangeStartMode: same-as-requested is success;
            // StartService: no-op when already running). Setting Automatic is the
            // fix for a found-but-Manual/Disabled daemon that would otherwise not
            // come back after a reboot.
            CimService.ChangeStartMode("sshd", CimService.StartMode.Automatic, logger);
            CimService.StartService("sshd", logger);
            logger.LogInformation("Ensured sshd is set to start Automatically and is running.");
        }, cancellationToken);

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

    public Task<bool> WriteSshdManagedConfigAsync(string directives, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            var sshDir = SshDir();
            Directory.CreateDirectory(sshDir);
            var configPath = Path.Combine(sshDir, "sshd_config");

            var existing = File.Exists(configPath)
                ? File.ReadAllText(configPath, new UTF8Encoding(false))
                : "";

            var updated = BuildSshdManagedConfig(existing, directives);
            if (updated is null)
            {
                logger.LogDebug("sshd_config managed block already current; no change.");
                return false;
            }

            File.WriteAllBytes(configPath, new UTF8Encoding(false).GetBytes(updated));
            logger.LogInformation("Wrote eryph-managed directives into sshd_config.");
            return true;
        }, cancellationToken);

    public Task StopSshdAsync(CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            if (CimService.GetState("sshd") is null)
                return; // not installed — nothing to stop.

            // Idempotent (no-op when already stopped). Used before host-key
            // regeneration: a running sshd holds the ssh_host_*_key files open,
            // so the regen delete fails and ssh-keygen then hangs on an
            // "overwrite?" prompt. Stopping first keeps the files unlocked.
            CimService.StopService("sshd", logger);
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

    // ---- Windows Package Manager (winget) — RFC 0020 ----
    //
    // Ported from gene:dbosoft/winget:install. The agent runs as LocalSystem,
    // so winget is PROVISIONED for all users (Add-AppxPackage is refused under
    // LocalSystem) and driven through a generated wrapper that puts the
    // App Installer + dependency package dirs on PATH.

    private static string WingetWrapperPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "eryph", "winget", "winget-system.ps1");

    public async Task<bool> IsWingetProvisionedAsync(CancellationToken cancellationToken)
    {
        const string script =
            "[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new()\n"
            + "$ErrorActionPreference = 'Stop'\n"
            + "$p = Get-AppxProvisionedPackage -Online | Where-Object DisplayName -eq 'Microsoft.DesktopAppInstaller'\n"
            + "if ($p) { 'provisioned' } else { 'absent' }\n";
        var result = await RunPowerShellAsync(script, cancellationToken).ConfigureAwait(false);
        return result.ExitCode == 0
            && result.StdOut.Contains("provisioned", StringComparison.Ordinal);
    }

    public async Task ProvisionWingetAsync(CancellationToken cancellationToken)
    {
        // Mirrors the fodder gene: OS-build gate (throws on Server 2019 /
        // pre-1809), download the latest release, provision for all users. The
        // LocalSystem wrapper is written separately by EnsureWingetWrapperAsync
        // so it is also refreshed when winget was already provisioned on the
        // image. A generous timeout guards against a hung download / DISM.
        var psi = BuildPowerShellCommandPsi(PackageScripts.WingetProvision());
        var result = await RunWithTimeoutAsync(psi, WingetProvisionTimeout, cancellationToken)
            .ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            var detail = result.StdErr.Trim();
            if (detail.Length == 0)
                detail = result.StdOut.Trim();
            throw new InvalidOperationException(
                $"winget provisioning failed (exit {result.ExitCode}): {detail}");
        }

        logger.LogInformation("Provisioned winget (App Installer) for all users.");
    }

    public async Task EnsureWingetWrapperAsync(CancellationToken cancellationToken)
    {
        // Idempotent: always (re)write the wrapper so it exists regardless of
        // whether winget was provisioned by us or shipped with the image, then
        // smoke-test that winget actually runs as LocalSystem through it.
        var wrapperPath = WingetWrapperPath;
        var wrapperDir = Path.GetDirectoryName(wrapperPath)!;

        // Security: the agent runs as LocalSystem and then EXECUTES this wrapper.
        // A non-admin who pre-created the directory (or any ancestor below
        // %ProgramData%) or the file could otherwise retain write access and
        // swap the script between write and execution (local privilege
        // escalation). Validate the whole directory chain from the trusted
        // %ProgramData% root — no reparse point, no non-privileged write — and
        // refuse an untrusted path rather than try to fix it. Then lock the
        // directory and a freshly-written file down to SYSTEM + Administrators.
        Directory.CreateDirectory(wrapperDir);
        if (UntrustedPathReason(wrapperDir) is { } wrapperReason)
            throw new InvalidOperationException(
                $"Refusing to use winget wrapper directory '{wrapperDir}': {wrapperReason}.");
        ApplySystemOnlyDirectoryAcl(new DirectoryInfo(wrapperDir));

        File.Delete(wrapperPath);
        // UTF-8 with BOM so Windows PowerShell 5.1 detects the encoding when it
        // runs the wrapper via -File (the content is ASCII, so this is belt-and-
        // suspenders rather than strictly required).
        await File.WriteAllTextAsync(
            wrapperPath,
            PackageScripts.WingetWrapper(),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
            cancellationToken).ConfigureAwait(false);
        ApplySystemOnlyFileAcl(new FileInfo(wrapperPath));

        var result = await RunWingetAsync(["--version"], cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            var detail = result.StdErr.Trim();
            if (detail.Length == 0)
                detail = result.StdOut.Trim();
            throw new InvalidOperationException(
                $"winget does not run as LocalSystem through the wrapper (exit {result.ExitCode}): {detail}");
        }

        logger.LogInformation("winget LocalSystem wrapper ready at {Path}.", wrapperPath);
    }

    // Owner SYSTEM; SYSTEM + Administrators FullControl; inheritance disabled and
    // all inherited/other rules stripped. Used to lock down the winget wrapper
    // directory and file so only privileged accounts can write what the agent
    // later executes as LocalSystem.
    private static void ApplySystemOnlyFileAcl(FileInfo info)
    {
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

    private static void ApplySystemOnlyDirectoryAcl(DirectoryInfo info)
    {
        var security = info.GetAccessControl();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, false, typeof(SecurityIdentifier)))
            security.RemoveAccessRuleSpecific(rule);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(
            system, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            admins, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.SetOwner(system);
        info.SetAccessControl(security);
    }

    // TrustedInstaller's service SID — a legitimate owner of OS-managed paths.
    private const string TrustedInstallerSid =
        "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";

    // Directory rights that let a non-privileged principal REPLACE or take over
    // existing content (and thus swap what the agent executes as LocalSystem).
    // We do NOT flag CreateFiles/CreateDirectories/AppendData on a directory:
    // %ProgramData% grants those to Users by default on every child, and they
    // only add NEW siblings — they can't modify, delete or take over our dirs.
    private const FileSystemRights DangerousDirectoryRights =
        FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles
        | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    // For a FILE, overwriting its contents in place is also dangerous. WriteData
    // / AppendData are the same 0x2 / 0x4 bits that mean (benign) CreateFiles /
    // CreateDirectories on a directory, so they must be checked for files only.
    private const FileSystemRights DangerousFileRights =
        DangerousDirectoryRights | FileSystemRights.WriteData | FileSystemRights.AppendData
        | FileSystemRights.WriteAttributes | FileSystemRights.WriteExtendedAttributes;

    private static bool IsPrivilegedSid(SecurityIdentifier sid) =>
        sid.IsWellKnown(WellKnownSidType.LocalSystemSid)
        || sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid)
        || sid.IsWellKnown(WellKnownSidType.CreatorOwnerSid)
        || string.Equals(sid.Value, TrustedInstallerSid, StringComparison.Ordinal);

    // The non-privileged principal the ACL grants a dangerous right to, or null.
    private static SecurityIdentifier? NonPrivilegedController(
        FileSystemSecurity security, FileSystemRights dangerous)
    {
        foreach (FileSystemAccessRule rule in
                 security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow)
                continue;
            // Inherit-only ACEs don't grant access to THIS node (only to its
            // children, which are validated on their own) — e.g. the volume
            // root's inherit-only "Authenticated Users: Modify" must not flag
            // the root itself. Skip them here.
            if (rule.PropagationFlags.HasFlag(PropagationFlags.InheritOnly))
                continue;
            if ((rule.FileSystemRights & dangerous) == 0)
                continue;
            if (rule.IdentityReference is SecurityIdentifier sid && !IsPrivilegedSid(sid))
                return sid;
        }

        return null;
    }

    // Returns why <paramref name="path"/> (or an ancestor up to a system root)
    // is not trusted for LocalSystem execution — a reparse point, a
    // non-privileged owner, or a non-privileged principal with replace/delete/
    // take-ownership (or, for a file, write) rights — or null when the whole
    // chain is trusted. Fails closed (returns a reason) on any error.
    internal static string? UntrustedPathReason(string path)
    {
        try
        {
            var anchors = TrustedRootAnchors();
            var current = Path.GetFullPath(path);
            while (!string.IsNullOrEmpty(current))
            {
                // System-provided roots (%ProgramData% / %ProgramFiles% /
                // %windir%) are trusted; stop. The VOLUME root is NOT shortcut
                // here — it is validated below (a data drive like D:\ may grant
                // non-privileged delete rights), then traversal ends at its null
                // parent.
                if (anchors.Contains(Path.TrimEndingDirectorySeparator(current)))
                    return null;

                FileSystemSecurity? security = null;
                var dangerous = DangerousDirectoryRights;
                if (Directory.Exists(current))
                {
                    var di = new DirectoryInfo(current);
                    if (di.Attributes.HasFlag(FileAttributes.ReparsePoint))
                        return $"'{current}' is a reparse point";
                    security = di.GetAccessControl();
                }
                else if (File.Exists(current))
                {
                    var fi = new FileInfo(current);
                    if (fi.Attributes.HasFlag(FileAttributes.ReparsePoint))
                        return $"'{current}' is a reparse point";
                    security = fi.GetAccessControl();
                    dangerous = DangerousFileRights;
                }

                if (security is not null)
                {
                    if (security.GetOwner(typeof(SecurityIdentifier)) is SecurityIdentifier owner
                        && !IsPrivilegedSid(owner))
                        return $"'{current}' is owned by a non-privileged account ({owner})";
                    if (NonPrivilegedController(security, dangerous) is { } controller)
                        return $"'{current}' grants modify/replace rights to a non-privileged account ({controller})";
                }

                var parent = Path.GetDirectoryName(current);
                if (string.IsNullOrEmpty(parent) || parent == current)
                    return null; // reached the volume root (validated above)
                current = parent;
            }

            return null;
        }
        catch (Exception ex)
        {
            return $"could not verify trust of '{path}': {ex.Message}";
        }
    }

    // True when <paramref name="path"/> and every ancestor up to a system root is
    // safe for LocalSystem execution (see <see cref="UntrustedPathReason"/>).
    internal static bool IsTrustedSystemPath(string path) => UntrustedPathReason(path) is null;

    private static HashSet<string> TrustedRootAnchors()
    {
        var anchors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(Environment.SpecialFolder folder)
        {
            var p = Environment.GetFolderPath(folder);
            if (!string.IsNullOrEmpty(p))
                anchors.Add(Path.TrimEndingDirectorySeparator(p));
        }
        Add(Environment.SpecialFolder.CommonApplicationData);
        Add(Environment.SpecialFolder.ProgramFiles);
        Add(Environment.SpecialFolder.ProgramFilesX86);
        Add(Environment.SpecialFolder.Windows);
        return anchors;
    }

    public async Task<RunCommandResult> RunWingetAsync(
        IReadOnlyList<string> argv,
        CancellationToken cancellationToken)
    {
        // Defence-in-depth: never execute the wrapper from an untrusted path
        // (parity with RunChocoAsync; EnsureWingetWrapperAsync also validates).
        if (UntrustedPathReason(WingetWrapperPath) is { } reason)
            throw new InvalidOperationException(
                $"Refusing to run the winget wrapper from an untrusted path: {reason}.");

        var psi = CreateUtf8Psi("powershell.exe");
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(WingetWrapperPath);
        foreach (var arg in argv)
            psi.ArgumentList.Add(arg);

        return await RunWithTimeoutAsync(psi, WingetRunTimeout, cancellationToken).ConfigureAwait(false);
    }

    // Generous safety-net timeouts so a hung installer / download cannot block
    // the Final stage forever. RunAsync kills the whole process tree on cancel.
    private static readonly TimeSpan WingetProvisionTimeout = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan WingetRunTimeout = TimeSpan.FromMinutes(15);

    // Distinct, non-success exit code reported when a winget run times out.
    private const int WingetTimeoutExitCode = -2;

    private ProcessStartInfo BuildPowerShellCommandPsi(string script)
    {
        var psi = CreateUtf8Psi("powershell.exe");
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(script);
        return psi;
    }

    // Runs a PowerShell script block via powershell.exe -Command with UTF-8
    // streams. Shared by the winget probe path.
    private Task<RunCommandResult> RunPowerShellAsync(string script, CancellationToken cancellationToken) =>
        RunAsync(BuildPowerShellCommandPsi(script), cancellationToken);

    // Runs a process with an upper time bound. An outer cancellation propagates
    // as OperationCanceledException; a timeout is translated into a non-zero
    // RunCommandResult (the tree is already killed by RunAsync).
    private async Task<RunCommandResult> RunWithTimeoutAsync(
        ProcessStartInfo psi,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        try
        {
            return await RunAsync(psi, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            logger.LogError("'{File}' timed out after {Minutes} minutes and was killed.", psi.FileName, timeout.TotalMinutes);
            return new RunCommandResult(
                WingetTimeoutExitCode, "", $"timed out after {timeout.TotalMinutes:0} minutes");
        }
    }

    // ---- Chocolatey — RFC 0021 ----
    //
    // Ported from gene:dbosoft/chocolatey:install. choco runs cleanly as
    // LocalSystem from C:\ProgramData\chocolatey — no wrapper, no impersonation.

    private static readonly TimeSpan ChocolateyBootstrapTimeout = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan ChocolateyRunTimeout = TimeSpan.FromMinutes(15);

    // Distinct exit code the bootstrap script emits when the installer reports a
    // .NET Framework reboot is required (choco is unusable until after it).
    private const int ChocolateyRebootExitCode = 3010;

    private static string ChocolateyExePath
    {
        get
        {
            var root = Environment.GetEnvironmentVariable("ChocolateyInstall");
            if (string.IsNullOrWhiteSpace(root))
                root = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "chocolatey");
            return Path.Combine(root, "bin", "choco.exe");
        }
    }

    public Task<bool> IsChocolateyInstalledAsync(CancellationToken cancellationToken) =>
        // "Installed" only when choco.exe is present AND on a trusted path. A
        // standard user can pre-create %ProgramData%\chocolatey\bin\choco.exe on
        // a machine without choco; treating that as installed would run a planted
        // binary as LocalSystem. An untrusted choco.exe reports "not installed"
        // so the module bootstraps (and BootstrapChocolateyAsync re-verifies the
        // result is trusted); RunChocoAsync refuses to execute an untrusted path.
        Task.FromResult(File.Exists(ChocolateyExePath) && IsTrustedSystemPath(ChocolateyExePath));

    public async Task<ChocolateyBootstrapResult> BootstrapChocolateyAsync(CancellationToken cancellationToken)
    {
        // Refuse to bootstrap into an ALREADY-EXISTING untrusted / reparse
        // install dir. We do NOT pre-create or lock the tree: Chocolatey's
        // installer self-manages %ChocolateyInstall% (creates it, copies files,
        // sets its own ACLs), and both a protected ACL and a bare SYSTEM
        // pre-create make install.ps1 complete without placing choco.exe
        // (verified on a real catlet). The post-install trust check and
        // RunChocoAsync's guard still refuse to execute an untrusted choco.exe,
        // and the installer runs as LocalSystem and force-copies its binaries.
        if (UntrustedPathReason(ChocolateyExePath) is { } preReason)
            throw new InvalidOperationException(
                $"Refusing to bootstrap Chocolatey into '{Path.GetDirectoryName(ChocolateyExePath)}': {preReason}.");

        var psi = BuildPowerShellCommandPsi(PackageScripts.ChocolateyBootstrap());
        var result = await RunWithTimeoutAsync(psi, ChocolateyBootstrapTimeout, cancellationToken)
            .ConfigureAwait(false);

        if (result.ExitCode == ChocolateyRebootExitCode)
        {
            logger.LogInformation("Chocolatey bootstrap reported a .NET Framework reboot is required.");
            return new ChocolateyBootstrapResult { RebootRequired = true };
        }

        if (result.ExitCode != 0)
        {
            var detail = result.StdErr.Trim();
            if (detail.Length == 0)
                detail = result.StdOut.Trim();
            throw new InvalidOperationException(
                $"Chocolatey bootstrap failed (exit {result.ExitCode}): {detail}");
        }

        // Verify choco actually landed where we will invoke it (honouring
        // %ChocolateyInstall%) AND that the path is trusted — refuse to accept a
        // bootstrap result sitting in a non-privileged-writable / reparse path.
        if (!File.Exists(ChocolateyExePath))
            throw new InvalidOperationException(
                $"Chocolatey bootstrap completed but choco.exe was not found at {ChocolateyExePath}.");
        if (UntrustedPathReason(ChocolateyExePath) is { } chocoReason)
            throw new InvalidOperationException(
                $"Refusing to trust Chocolatey at '{ChocolateyExePath}': {chocoReason}.");

        logger.LogInformation("Bootstrapped Chocolatey.");
        return new ChocolateyBootstrapResult { RebootRequired = false };
    }

    public async Task<RunCommandResult> RunChocoAsync(
        IReadOnlyList<string> argv,
        CancellationToken cancellationToken)
    {
        // Never execute choco from an untrusted path (defence-in-depth alongside
        // IsChocolateyInstalledAsync / BootstrapChocolateyAsync).
        if (!IsTrustedSystemPath(ChocolateyExePath))
            throw new InvalidOperationException(
                $"Refusing to run Chocolatey from an untrusted path '{ChocolateyExePath}'.");

        var psi = CreateUtf8Psi(ChocolateyExePath);
        foreach (var arg in argv)
            psi.ArgumentList.Add(arg);
        return await RunWithTimeoutAsync(psi, ChocolateyRunTimeout, cancellationToken).ConfigureAwait(false);
    }

    // ---- DSC v3 (microsoft/DSC — dsc.exe) — RFC 0025 ----
    //
    // Replaces gene:dbosoft/windsc minus its DSC v1 baggage: dsc.exe is a native
    // engine (no MOF compile, no NuGet bootstrap, no self-signed cert). The agent
    // runs as LocalSystem and then EXECUTES dsc.exe AND reads the config document
    // it wrote, so both the executable and the document directory are
    // trust-validated against the LocalSystem-LPE surface the package modules use.

    private static readonly TimeSpan DscInstallTimeout = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan DscRunTimeout = TimeSpan.FromMinutes(30);

    private static string DscProgramFilesDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dsc");

    private static string DscProgramFilesExePath =>
        Path.Combine(DscProgramFilesDirectory, "dsc.exe");

    private static string DscDocumentDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "eryph", "dsc");

    public Task<string?> ResolveDscExecutableAsync(string? explicitPath, CancellationToken cancellationToken) =>
        Task.Run<string?>(() =>
        {
            // 1. Operator-supplied explicit path (install_source: path). Must
            //    exist and be trusted — we refuse a planted/untrusted binary.
            if (!string.IsNullOrWhiteSpace(explicitPath))
            {
                var expanded = Environment.ExpandEnvironmentVariables(explicitPath);
                if (!File.Exists(expanded))
                {
                    logger.LogWarning("dsc.exe was not found at the configured path '{Path}'.", expanded);
                    return null;
                }
                if (UntrustedPathReason(expanded) is { } reason)
                {
                    logger.LogWarning("Refusing to use dsc.exe at '{Path}': {Reason}.", expanded, reason);
                    return null;
                }
                return expanded;
            }

            // 2. The GitHub-release install target.
            if (File.Exists(DscProgramFilesExePath) && IsTrustedSystemPath(DscProgramFilesExePath))
                return DscProgramFilesExePath;

            // 3. A non-symlink dsc.exe on the refreshed machine PATH in a trusted
            //    directory. We deliberately do NOT follow a PATH symlink to its
            //    target: a standard user who can write to any PATH directory
            //    could plant a `dsc.exe` link repointing at a different trusted
            //    binary and have us run it as LocalSystem (a confused-deputy LPE).
            //    IsTrustedSystemPath rejects reparse points, so a planted link is
            //    refused here; the winget install locations are resolved directly
            //    below instead of via their PATH alias.
            var onPath = ResolveExecutableOnRefreshedPath("dsc.exe");
            if (!string.Equals(onPath, "dsc.exe", StringComparison.Ordinal)
                && File.Exists(onPath) && IsTrustedSystemPath(onPath))
                return onPath;

            // 4. The winget zip/portable install tree (machine scope) under
            //    %ProgramFiles%\WinGet\Packages\Microsoft.DSC_* — the real
            //    executable, not the Links symlink that points at it.
            var inPackages = FindDscInWingetPackages();
            if (inPackages is not null && IsTrustedSystemPath(inPackages))
                return inPackages;

            // 5. The winget MSIX install under %ProgramFiles%\WindowsApps. The
            //    package identity is Microsoft.DesiredStateConfiguration (the
            //    winget id Microsoft.DSC is not the MSIX identity).
            var inWindowsApps = FindDscInWindowsApps();
            if (inWindowsApps is not null && IsTrustedSystemPath(inWindowsApps))
                return inWindowsApps;

            return null;
        }, cancellationToken);

    // Best-effort scan of the machine-scope winget portable store for a
    // Microsoft.DSC install: %ProgramFiles%\WinGet\Packages\Microsoft.DSC_*\dsc.exe
    // (the zip/portable installer unpacks dsc.exe into the package dir).
    private static string? FindDscInWingetPackages()
    {
        try
        {
            var packages = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WinGet", "Packages");
            if (!Directory.Exists(packages))
                return null;
            foreach (var dir in Directory.EnumerateDirectories(packages, "Microsoft.DSC_*"))
            {
                // The portable zip may nest dsc.exe one level down (arch folder).
                var candidate = Directory
                    .EnumerateFiles(dir, "dsc.exe", SearchOption.AllDirectories)
                    .FirstOrDefault();
                if (candidate is not null)
                    return candidate;
            }
        }
        catch
        {
            // Access denied / odd layout — fall through.
        }

        return null;
    }

    // Best-effort scan of the machine-scope WindowsApps store for a winget-
    // installed (MSIX) dsc.exe. The DSC MSIX package identity is
    // Microsoft.DesiredStateConfiguration (confirmed from its AppxManifest), not
    // the winget id Microsoft.DSC. %ProgramFiles%\WindowsApps is readable only by
    // privileged accounts; the agent runs as LocalSystem, so this succeeds where
    // a per-user execution alias would not be visible.
    private static string? FindDscInWindowsApps()
    {
        try
        {
            var windowsApps = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
            if (!Directory.Exists(windowsApps))
                return null;
            foreach (var dir in Directory.EnumerateDirectories(windowsApps, "Microsoft.DesiredStateConfiguration*"))
            {
                var candidate = Path.Combine(dir, "dsc.exe");
                if (File.Exists(candidate))
                    return candidate;
            }
        }
        catch
        {
            // Access denied / odd layout — fall through to "not found".
        }

        return null;
    }

    public async Task InstallDscFromGithubReleaseAsync(string? version, CancellationToken cancellationToken)
    {
        // RESERVE the install tree before downloading (same race the Chocolatey
        // bootstrap guards): %ProgramFiles% lets standard users create dirs, so
        // create it ourselves, reject an already-existing untrusted / reparse
        // tree, and lock it to SYSTEM + Administrators before extracting into it.
        var target = DscProgramFilesDirectory;
        Directory.CreateDirectory(target);
        if (UntrustedPathReason(target) is { } preReason)
            throw new InvalidOperationException(
                $"Refusing to install DSC into '{target}': {preReason}.");
        ApplySystemOnlyDirectoryAcl(new DirectoryInfo(target));

        var arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.Arm64 => "aarch64",
            _ => "x86_64",
        };

        using var timeoutCts = new CancellationTokenSource(DscInstallTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        using var http = new HttpClient();
        // GitHub's REST API requires a User-Agent.
        http.DefaultRequestHeaders.UserAgent.ParseAdd("eryph-guest-services");

        var apiUrl = string.IsNullOrWhiteSpace(version) || IsLatestToken(version)
            ? "https://api.github.com/repos/PowerShell/DSC/releases/latest"
            : $"https://api.github.com/repos/PowerShell/DSC/releases/tags/v{version!.TrimStart('v')}";

        string releaseJson;
        try
        {
            releaseJson = await http.GetStringAsync(apiUrl, linked.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException($"Failed to query the DSC release metadata: {ex.Message}", ex);
        }

        var assetUrl = DscSupport.SelectWindowsReleaseAsset(releaseJson, arch);

        var tempDir = Path.Combine(
            Path.GetTempPath(), "egs-dsc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var zipPath = Path.Combine(tempDir, "dsc.zip");
            byte[] zipBytes;
            try
            {
                zipBytes = await http.GetByteArrayAsync(assetUrl, linked.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new InvalidOperationException($"Failed to download the DSC release: {ex.Message}", ex);
            }

            await File.WriteAllBytesAsync(zipPath, zipBytes, linked.Token).ConfigureAwait(false);
            ZipFile.ExtractToDirectory(zipPath, target, overwriteFiles: true);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best effort */ }
        }

        // Re-harden: files extracted into the (already-locked) directory inherit
        // its ACL, but set the owner/ACL on the executable explicitly too.
        if (!File.Exists(DscProgramFilesExePath))
            throw new InvalidOperationException(
                $"DSC install completed but dsc.exe was not found at '{DscProgramFilesExePath}'.");
        ApplySystemOnlyFileAcl(new FileInfo(DscProgramFilesExePath));
        if (UntrustedPathReason(DscProgramFilesExePath) is { } reason)
            throw new InvalidOperationException(
                $"Refusing to trust the installed dsc.exe at '{DscProgramFilesExePath}': {reason}.");

        logger.LogInformation("Installed DSC v3 to {Path}.", DscProgramFilesExePath);
    }

    private static bool IsLatestToken(string? version) =>
        string.IsNullOrWhiteSpace(version)
        || string.Equals(version.Trim(), "latest", StringComparison.OrdinalIgnoreCase);

    public Task<bool> IsPowerShell7AvailableAsync(CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            var resolved = ResolveExecutableOnRefreshedPath("pwsh.exe");
            if (!string.Equals(resolved, "pwsh.exe", StringComparison.Ordinal) && File.Exists(resolved))
                return true;
            // pwsh's default per-machine install location is not always on the
            // service PATH; check it directly as a fallback.
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            return File.Exists(Path.Combine(programFiles, "PowerShell", "7", "pwsh.exe"));
        }, cancellationToken);

    public async Task<string> WriteDscDocumentAsync(
        string fileName,
        string content,
        CancellationToken cancellationToken)
    {
        var path = PrepareDscDocumentPath(fileName);
        File.Delete(path);
        await File.WriteAllTextAsync(
            path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken)
            .ConfigureAwait(false);
        ApplySystemOnlyFileAcl(new FileInfo(path));
        return path;
    }

    public async Task<string> DownloadDscDocumentAsync(
        string fileName,
        string url,
        string? checksum,
        CancellationToken cancellationToken)
    {
        var path = PrepareDscDocumentPath(fileName);

        using var timeoutCts = new CancellationTokenSource(DscInstallTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("eryph-guest-services");

        byte[] bytes;
        try
        {
            bytes = await http.GetByteArrayAsync(url, linked.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"Failed to download the DSC document from '{url}': {ex.Message}", ex);
        }

        if (!string.IsNullOrWhiteSpace(checksum)
            && DscSupport.Sha256Mismatch(bytes, checksum) is { } mismatch)
            throw new InvalidOperationException($"DSC document '{url}' {mismatch}");

        File.Delete(path);
        await File.WriteAllBytesAsync(path, bytes, cancellationToken).ConfigureAwait(false);
        ApplySystemOnlyFileAcl(new FileInfo(path));
        return path;
    }

    // Ensures the SYSTEM-only DSC document directory exists and is trusted, and
    // returns the full path a document with the given (sanitized) file name
    // should be written to. The agent feeds these documents to dsc.exe as
    // LocalSystem, so the directory is hardened like the winget wrapper dir.
    private string PrepareDscDocumentPath(string fileName)
    {
        var safeName = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(safeName))
            throw new ArgumentException("DSC document file name is empty.", nameof(fileName));

        var dir = DscDocumentDirectory;
        Directory.CreateDirectory(dir);
        if (UntrustedPathReason(dir) is { } reason)
            throw new InvalidOperationException(
                $"Refusing to use the DSC document directory '{dir}': {reason}.");
        ApplySystemOnlyDirectoryAcl(new DirectoryInfo(dir));
        return Path.Combine(dir, safeName);
    }

    public async Task<DscApplyResult> RunDscConfigAsync(
        string dscExePath,
        string documentPath,
        string verb,
        CancellationToken cancellationToken)
    {
        // Defence-in-depth: never execute dsc.exe from an untrusted path
        // (ResolveDscExecutableAsync already validated it).
        if (UntrustedPathReason(dscExePath) is { } reason)
            throw new InvalidOperationException(
                $"Refusing to run dsc.exe from an untrusted path: {reason}.");

        // dsc.exe enforces the document's resource actions as LocalSystem, so the
        // document is as privileged as the executable. Documents we stage land in
        // the hardened SYSTEM-only DSC directory, but an operator-supplied
        // document_path may sit in a standard-user-writable location where its
        // contents could be swapped before we run it — trust-check it too.
        if (UntrustedPathReason(documentPath) is { } documentReason)
            throw new InvalidOperationException(
                $"Refusing to apply a DSC document from an untrusted path: {documentReason}.");

        var psi = CreateUtf8Psi(dscExePath);
        psi.ArgumentList.Add("config");
        psi.ArgumentList.Add(verb);
        psi.ArgumentList.Add("--file");
        psi.ArgumentList.Add(documentPath);
        // dsc.exe (and the resources / pwsh it spawns) must see tools installed
        // earlier this boot — the service's startup PATH is stale. The resolver
        // already probes the refreshed PATH; the launch must use it too.
        ApplyRefreshedPath(psi);

        var result = await RunWithTimeoutAsync(psi, DscRunTimeout, cancellationToken).ConfigureAwait(false);
        // Only trust the reboot signal on a successful run; a non-zero exit is a
        // failure the module surfaces regardless.
        var reboot = result.ExitCode == 0 && DscSupport.RequiresReboot(result.StdOut);
        return new DscApplyResult
        {
            ExitCode = result.ExitCode,
            RebootRequested = reboot,
            JsonOutput = result.StdOut,
            StdErr = result.StdErr,
        };
    }

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

    internal const string SshdManagedBeginMarker = "# BEGIN eryph guest-services (managed)";
    internal const string SshdManagedEndMarker = "# END eryph guest-services (managed)";
    // The directive our earlier releases prepended. sshd_config `Include`
    // requires OpenSSH 8.2+, but the Windows-inbox sshd (8.1p1 on Server 2022)
    // rejects it and refuses to start — so we no longer use it and strip any
    // copy we previously wrote when re-provisioning such a guest.
    private const string LegacySshdIncludeDirective = "Include sshd_config.d/*.conf";

    // internal for unit testing — pure form of the sshd_config write. Prepends
    // our directives in a marked, managed block so their first-obtained-value
    // settings win over shipped directives (and the trailing
    // `Match Group administrators` block). We inline the directives directly
    // instead of an `Include` drop-in because `Include` is unsupported by
    // OpenSSH < 8.2 (the inbox Windows sshd), which then fails to start.
    // Returns the new content, or null when the file already matches (idempotent).
    internal static string? BuildSshdManagedConfig(string existing, string directives)
    {
        var body = StripSshdManagedArtifacts(NormalizeLf(existing)).TrimEnd('\n');
        var block = SshdManagedBeginMarker + "\n"
            + NormalizeLf(directives).Trim('\n') + "\n"
            + SshdManagedEndMarker + "\n";
        var result = body.Length == 0 ? block : block + body + "\n";

        return string.Equals(result, NormalizeLf(existing), StringComparison.Ordinal) ? null : result;
    }

    // Removes any prior eryph managed block and the legacy Include line, so the
    // rebuild replaces (never duplicates) our directives and cleans up guests
    // provisioned by the old Include/drop-in approach. Only a properly
    // terminated BEGIN..END block is stripped: a lone BEGIN (hand-edited or
    // truncated file) is kept as-is rather than silently eating the rest of the
    // file (which includes the vendor `Match Group administrators` block).
    private static string StripSshdManagedArtifacts(string normalized)
    {
        var kept = new List<string>();
        var block = new List<string>();
        var inBlock = false;
        foreach (var line in normalized.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!inBlock && trimmed == SshdManagedBeginMarker)
            {
                inBlock = true;
                block.Clear();
                block.Add(line);
                continue;
            }
            if (inBlock)
            {
                block.Add(line);
                if (trimmed == SshdManagedEndMarker)
                {
                    // Properly closed block — drop it entirely.
                    inBlock = false;
                    block.Clear();
                }
                continue;
            }
            if (string.Equals(trimmed, LegacySshdIncludeDirective, StringComparison.OrdinalIgnoreCase))
                continue;
            kept.Add(line);
        }

        // An unterminated block (corrupt/hand-edited file): keep its content so
        // the rest of the file survives, but drop the orphaned BEGIN marker
        // line itself (block[0]) so we don't leave a stray marker behind.
        if (inBlock)
        {
            for (var i = 1; i < block.Count; i++)
                kept.Add(block[i]);
        }

        return string.Join('\n', kept);
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
