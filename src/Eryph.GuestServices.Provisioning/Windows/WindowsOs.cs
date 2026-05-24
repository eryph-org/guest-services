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
        // cmd.exe /c "<complex command>" has notoriously broken quoting rules:
        // embedded `|`, `&`, `<`, `>`, escaped quotes and similar shell-special
        // characters routinely cause cmd to mis-parse the command line. The
        // robust pattern (also used by cloudbase-init) is to write the command
        // verbatim to a temporary .cmd file and run cmd.exe /c on the FILE —
        // there's no quoting at the parent level, so nothing can be mangled.
        var tempScript = Path.Combine(
            Path.GetTempPath(),
            "egs-runcmd-" + Guid.NewGuid().ToString("N") + ".cmd");
        await File.WriteAllTextAsync(
            tempScript,
            "@echo off\r\n" + command + "\r\n",
            cancellationToken).ConfigureAwait(false);

        try
        {
            var psi = new ProcessStartInfo("cmd.exe")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(tempScript);

            return await RunAsync(psi, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try { File.Delete(tempScript); } catch { /* best-effort cleanup */ }
        }
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

    public Task<IReadOnlyList<NetworkAdapterInfo>> GetNetworkAdaptersAsync(CancellationToken cancellationToken) =>
        Task.Run(() => CimNetworking.EnumerateAdapters(), cancellationToken);

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
        var psi = new ProcessStartInfo("powershell.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
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

    private static string SlmgrPath()
    {
        var systemRoot = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
        return Path.Combine(systemRoot, "System32", "slmgr.vbs");
    }

    private async Task RunOrThrowAsync(
        string fileName,
        IReadOnlyList<string> argv,
        CancellationToken cancellationToken,
        bool ignoreNonZero = false)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in argv) psi.ArgumentList.Add(a);
        var result = await RunAsync(psi, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0 && !ignoreNonZero)
        {
            throw new InvalidOperationException(
                $"{fileName} {string.Join(' ', argv)} exited {result.ExitCode}: {result.StdErr.Trim()}");
        }
    }

    // internal for unit testing — the PowerShell-string-building logic is a
    // non-trivial composition root that we want exercised directly.
    internal static string BuildLocaleScript(LocaleSpec spec)
    {
        // The script is intentionally compact: it queries current state, sets
        // only what's different, and emits a single `REBOOT_REQUIRED=...` line
        // for the caller to parse. Any cmdlet failure terminates via -ErrorAction
        // Stop so RunAsync sees a non-zero exit.
        var sb = new StringBuilder();
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

    private static string EscapePsSingleQuoted(string value) => value.Replace("'", "''");

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
            return Path.GetFullPath(unixPath);
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

        // Canonicalize and verify the result still anchors under C:\. The
        // mapping documented at the top of this file lands everything below
        // C:\ (including /tmp -> %TEMP% and /var -> %ProgramData%, both of
        // which are C:\ subtrees on a default Windows install). Anything that
        // canonicalizes elsewhere is rejected.
        var full = Path.GetFullPath(candidate);
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
