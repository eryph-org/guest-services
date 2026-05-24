using Microsoft.Extensions.Logging;

namespace Eryph.GuestServices.Provisioning.Windows;

/// <summary>
/// Decorator over <see cref="IWindowsOs"/> used by <c>egs-provisioning run --dry-run</c>.
/// Read methods pass through to the wrapped implementation so handlers see the
/// real guest state; write methods log a structured "DRY-RUN would..." message
/// at Information level and return success-like values without mutating anything.
/// </summary>
/// <remarks>
/// The decorator is registered with <see cref="WindowsOs"/> as its inner
/// dependency in production (see <c>ProvisioningContainerBuilder</c>). Tests
/// substitute the inner <see cref="IWindowsOs"/> to assert that write methods
/// are intercepted.
/// </remarks>
internal sealed class DryRunWindowsOs(IWindowsOs inner, ILogger<DryRunWindowsOs> logger) : IWindowsOs
{
    // ---- Reads pass through ----

    public Task<string> GetComputerNameAsync(CancellationToken cancellationToken) =>
        inner.GetComputerNameAsync(cancellationToken);

    public Task<bool> LocalUserExistsAsync(string name, CancellationToken cancellationToken) =>
        inner.LocalUserExistsAsync(name, cancellationToken);

    public Task<bool> LocalGroupExistsAsync(string name, CancellationToken cancellationToken) =>
        inner.LocalGroupExistsAsync(name, cancellationToken);

    public string TranslateUnixPath(string unixPath) => inner.TranslateUnixPath(unixPath);

    public Task<IReadOnlyList<NetworkAdapterInfo>> GetNetworkAdaptersAsync(CancellationToken cancellationToken) =>
        inner.GetNetworkAdaptersAsync(cancellationToken);

    // ---- Writes are intercepted ----

    public Task<SetComputerNameResult> SetComputerNameAsync(string newName, CancellationToken cancellationToken)
    {
        logger.LogInformation("DRY-RUN would set computer name to {NewName}", newName);
        // Reporting AlreadySet keeps handlers from triggering a reboot in dry-run.
        return Task.FromResult(SetComputerNameResult.AlreadySet);
    }

    public Task CreateLocalUserAsync(LocalUserSpec spec, CancellationToken cancellationToken)
    {
        logger.LogInformation("DRY-RUN would create local user {User}", spec.Name);
        return Task.CompletedTask;
    }

    public Task UpdateLocalUserAsync(LocalUserSpec spec, CancellationToken cancellationToken)
    {
        logger.LogInformation("DRY-RUN would update local user {User}", spec.Name);
        return Task.CompletedTask;
    }

    public Task SetLocalUserPasswordAsync(
        string name,
        string password,
        bool mustChangeAtNextLogon,
        CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "DRY-RUN would set password for user {User} (mustChangeAtNextLogon={MustChange})",
            name,
            mustChangeAtNextLogon);
        return Task.CompletedTask;
    }

    public Task CreateLocalGroupAsync(string name, CancellationToken cancellationToken)
    {
        logger.LogInformation("DRY-RUN would create local group {Group}", name);
        return Task.CompletedTask;
    }

    public Task AddUserToGroupAsync(string userName, string groupName, CancellationToken cancellationToken)
    {
        logger.LogInformation("DRY-RUN would add user {User} to group {Group}", userName, groupName);
        return Task.CompletedTask;
    }

    public Task EnsureUserInAdministratorsAsync(string userName, CancellationToken cancellationToken)
    {
        logger.LogInformation("DRY-RUN would add user {User} to local Administrators", userName);
        return Task.CompletedTask;
    }

    public Task EnsureDirectoryAsync(string windowsPath, CancellationToken cancellationToken)
    {
        logger.LogInformation("DRY-RUN would ensure directory {Path}", windowsPath);
        return Task.CompletedTask;
    }

    public Task WriteFileAsync(string windowsPath, byte[] content, bool append, CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "DRY-RUN would write file {Path} ({Bytes} bytes, append={Append})",
            windowsPath,
            content.Length,
            append);
        return Task.CompletedTask;
    }

    public Task SetFileOwnerAsync(string windowsPath, string owner, CancellationToken cancellationToken)
    {
        logger.LogInformation("DRY-RUN would set owner of {Path} to {Owner}", windowsPath, owner);
        return Task.CompletedTask;
    }

    public Task SetPosixPermissionsAsync(
        string windowsPath,
        string permissions,
        string? owner,
        CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "DRY-RUN would apply POSIX permissions {Perms} to {Path} (owner={Owner})",
            permissions, windowsPath, owner ?? "<none>");
        return Task.CompletedTask;
    }

    public Task SetUserSshAuthorizedKeysAsync(
        string userName,
        IReadOnlyList<string> keys,
        CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "DRY-RUN would write {KeyCount} SSH authorized key(s) for user {User}",
            keys.Count,
            userName);
        return Task.CompletedTask;
    }

    public Task<RunCommandResult> RunShellCommandAsync(string command, CancellationToken cancellationToken)
    {
        logger.LogInformation("DRY-RUN would run shell command: {Command}", command);
        return Task.FromResult(new RunCommandResult(0, "", ""));
    }

    public Task<RunCommandResult> RunArgvCommandAsync(IReadOnlyList<string> argv, CancellationToken cancellationToken)
    {
        logger.LogInformation("DRY-RUN would run argv command: {Argv}", string.Join(" ", argv));
        return Task.FromResult(new RunCommandResult(0, "", ""));
    }

    public Task EnableDhcpAsync(int interfaceIndex, CancellationToken cancellationToken)
    {
        logger.LogInformation("DRY-RUN would enable DHCP on interface index {Index}", interfaceIndex);
        return Task.CompletedTask;
    }

    public Task DisableDhcpAsync(int interfaceIndex, CancellationToken cancellationToken)
    {
        logger.LogInformation("DRY-RUN would disable DHCP on interface index {Index}", interfaceIndex);
        return Task.CompletedTask;
    }

    public Task SetStaticIpv4AddressesAsync(
        int interfaceIndex,
        IReadOnlyList<string> addresses,
        CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "DRY-RUN would set static IPv4 addresses on interface index {Index}: {Addresses}",
            interfaceIndex, string.Join(", ", addresses));
        return Task.CompletedTask;
    }

    public Task SetIpv4DefaultGatewayAsync(
        int interfaceIndex,
        string? gateway,
        CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "DRY-RUN would set IPv4 default gateway on interface index {Index} to {Gateway}",
            interfaceIndex, gateway ?? "<clear>");
        return Task.CompletedTask;
    }

    public Task SetDnsServersAsync(
        int interfaceIndex,
        IReadOnlyList<string> dnsServers,
        CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "DRY-RUN would set DNS servers on interface index {Index} to {Servers}",
            interfaceIndex,
            dnsServers.Count == 0 ? "<reset>" : string.Join(", ", dnsServers));
        return Task.CompletedTask;
    }

    public Task SetInterfaceMtuAsync(int interfaceIndex, int mtu, CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "DRY-RUN would set MTU on interface index {Index} to {Mtu}", interfaceIndex, mtu);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<VolumeExtendResult>> ExtendVolumesAsync(
        IReadOnlySet<char>? driveLetterFilter,
        CancellationToken cancellationToken)
    {
        var target = driveLetterFilter is null
            ? "all growable volumes"
            : string.Join(", ", driveLetterFilter);
        logger.LogInformation("DRY-RUN would extend partitions for {Target}", target);
        return Task.FromResult<IReadOnlyList<VolumeExtendResult>>([]);
    }

    public Task ConfigureNtpClientAsync(
        bool enabled,
        IReadOnlyList<string> peers,
        CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "DRY-RUN would configure NTP (enabled={Enabled}, peers={Peers})",
            enabled,
            peers.Count == 0 ? "<none>" : string.Join(", ", peers));
        return Task.CompletedTask;
    }

    public Task SetRealTimeClockUtcAsync(bool utc, CancellationToken cancellationToken)
    {
        logger.LogInformation("DRY-RUN would set RealTimeIsUniversal={Utc}", utc);
        return Task.CompletedTask;
    }

    public Task SetTimezoneAsync(string windowsTimezoneId, CancellationToken cancellationToken)
    {
        logger.LogInformation("DRY-RUN would set timezone to {TimezoneId}", windowsTimezoneId);
        return Task.CompletedTask;
    }

    public Task<LocaleApplyResult> ApplyLocaleAsync(LocaleSpec spec, CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "DRY-RUN would apply locale (locale={Locale}, keyboard={Keyboard})",
            spec.Locale ?? "<unchanged>",
            spec.KeyboardLayout ?? "<unchanged>");
        // Dry-run never claims reboot — keeps the run from triggering a real reboot.
        return Task.FromResult(new LocaleApplyResult { RebootRequired = false });
    }

    public Task ApplyLicenseAsync(LicenseSpec spec, CancellationToken cancellationToken)
    {
        // Never log the product key in full — operators copy logs around and we
        // don't want to leak licensing material into ticket trackers.
        var maskedKey = string.IsNullOrEmpty(spec.ProductKey)
            ? "<unchanged>"
            : $"***{spec.ProductKey[^4..]}";
        logger.LogInformation(
            "DRY-RUN would apply license (productKey={Key}, kmsHost={Host}, clearKms={Clear}, activate={Activate})",
            maskedKey,
            spec.KmsHost ?? "<unchanged>",
            spec.ClearKmsHost,
            spec.Activate);
        return Task.CompletedTask;
    }

    public Task<RearmResult> RearmLicenseAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("DRY-RUN would run slmgr /rearm");
        // Dry-run never claims reboot so a what-if doesn't trigger restart paths.
        return Task.FromResult(new RearmResult { RebootRequired = false });
    }

    public Task<string?> ResolveVolumeActivationKeyAsync(
        VolumeActivationKeyType type,
        CancellationToken cancellationToken) =>
        // Reads only — pass through to the wrapped real implementation so
        // dry-run still sees what would have been resolved on this guest.
        inner.ResolveVolumeActivationKeyAsync(type, cancellationToken);

    public Task<bool> IsEvaluationLicenseAsync(CancellationToken cancellationToken) =>
        // Read-only — pass through.
        inner.IsEvaluationLicenseAsync(cancellationToken);

    public Task RequestPowerStateAsync(PowerStateRequest request, CancellationToken cancellationToken)
    {
        // Critical to NOT actually shutdown in dry-run — the whole point
        // of dry-run is "tell me what would happen". Log + skip.
        logger.LogInformation(
            "DRY-RUN would schedule {Action} in {Delay}s (message='{Msg}')",
            request.Action, request.DelaySeconds, request.Message ?? "<none>");
        return Task.CompletedTask;
    }
}
