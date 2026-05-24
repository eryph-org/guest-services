using System.Text;
using Eryph.GuestServices.Provisioning.Reporting;
using Eryph.GuestServices.Provisioning.Reporting.Events;
using Eryph.GuestServices.Provisioning.Stages;
using Eryph.GuestServices.Provisioning.UserData;
using Eryph.GuestServices.Provisioning.Windows;
using Microsoft.Extensions.Logging;
using CloudConfigModel = Eryph.GuestServices.CloudConfig.CloudConfig;

namespace Eryph.GuestServices.Provisioning.Modules;

/// <summary>
/// Cloud-init <c>cc_ssh</c> equivalent for the OS-level Win32-OpenSSH daemon
/// at <c>C:\ProgramData\ssh\</c> (NOT the egs-service Hyper-V-socket transport).
/// Covers host keys, the <c>sshd_config.d</c> drop-in (<c>ssh_pwauth</c>,
/// <c>disable_root</c>), per-user/top-level <c>authorized_keys</c> and host-key
/// fingerprint reporting. See RFC 0018.
/// </summary>
[Stage(Stage.Config, Order = 2, Frequency = ModuleFrequency.PerInstance)]
internal sealed class SshModule(
    ILogger<SshModule> logger,
    IReportingDispatcher reporter,
    IDefaultUserResolver defaultUser) : IModule
{
    /// <summary>Drop-in file name under <c>sshd_config.d</c>.</summary>
    private const string DropInFileName = "50-eryph.conf";

    /// <summary>
    /// Default host-key set when <c>ssh_genkeytypes</c> is not supplied.
    /// ed25519-first, DSA dropped (removed in OpenSSH 9.8). RFC 0018.
    /// </summary>
    private static readonly string[] DefaultGenKeyTypes = ["ed25519", "ecdsa", "rsa"];

    public async Task<ModuleOutcome> ApplyAsync(
        ResolvedUserData userData,
        IModuleContext context,
        CancellationToken cancellationToken)
    {
        var config = userData.CloudConfig;

        // 1. Detect sshd; optionally install.
        var installed = await context.Os.IsSshdInstalledAsync(cancellationToken).ConfigureAwait(false);
        if (!installed && config.Ssh?.InstallOpenssh == true)
        {
            try
            {
                logger.LogInformation("sshd not present; installing Win32-OpenSSH server (ssh.install_openssh).");
                await context.Os.InstallOpenSshServerAsync(cancellationToken).ConfigureAwait(false);
                installed = true;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to install the Win32-OpenSSH server.");
                return ModuleOutcome.Fail("install of the Win32-OpenSSH server failed.", ex);
            }
        }

        if (!installed)
        {
            // No daemon to configure — authorized_keys are still useful (they're
            // read per-connection once an sshd exists), so write them and bail
            // before any host-key / config / restart work.
            logger.LogInformation(
                "sshd not present; writing authorized_keys only, skipping host-key/config/restart.");
            await ProcessAuthorizedKeysAsync(config, context, cancellationToken).ConfigureAwait(false);
            return ModuleOutcome.Ok();
        }

        IReadOnlyList<SshHostKeyFingerprint> reportableFingerprints = [];
        bool hostKeysChanged;
        bool configChanged;

        try
        {
            // 2. Host keys.
            (hostKeysChanged, reportableFingerprints) =
                await ProcessHostKeysAsync(config, context, cancellationToken).ConfigureAwait(false);

            // 3. Drop-in sshd_config.
            configChanged = await ProcessDropInAsync(config, context, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to apply ssh host-key / config on an installed sshd.");
            return ModuleOutcome.Fail("ssh host-key / config write failed.", ex);
        }

        // 4. authorized_keys (does not need a restart — sshd reads them per-connection).
        await ProcessAuthorizedKeysAsync(config, context, cancellationToken).ConfigureAwait(false);

        // 5. Restart only when host keys or sshd_config actually changed.
        if (hostKeysChanged || configChanged)
        {
            logger.LogInformation("Restarting sshd to pick up host-key / config changes.");
            await context.Os.RestartSshdAsync(cancellationToken).ConfigureAwait(false);
        }

        // 6. Report fingerprints (gated by ssh.emit_keys_to_console, default true).
        if (reportableFingerprints.Count > 0 && config.Ssh?.EmitKeysToConsole != false)
        {
            await reporter.EmitAsync(
                new ReportingEvent.SshHostKeysReported(reportableFingerprints)
                {
                    Origin = $"module:{nameof(SshModule)}",
                },
                cancellationToken).ConfigureAwait(false);
        }

        return ModuleOutcome.Ok();
    }

    /// <summary>
    /// Host-key step. Returns whether the host-key material changed and the
    /// fingerprints that should be reported.
    /// </summary>
    /// <remarks>
    /// We have no OS primitive to enumerate existing host keys, so we cannot
    /// "generate only the missing types". Because the module is
    /// <see cref="ModuleFrequency.PerInstance"/> it runs once per instance and
    /// the semaphore prevents re-runs — so unconditional generation on the
    /// first instance boot is correct and not repeated. Supplied
    /// <c>ssh_keys</c> are written verbatim; we have no fingerprints for them
    /// (that would need an OS call we don't have), so only the regenerated set
    /// is reported.
    /// </remarks>
    private async Task<(bool Changed, IReadOnlyList<SshHostKeyFingerprint> Fingerprints)> ProcessHostKeysAsync(
        CloudConfigModel config,
        IModuleContext context,
        CancellationToken cancellationToken)
    {
        if (config.SshKeys is { Count: > 0 })
        {
            var wrote = await WriteSuppliedHostKeysAsync(config.SshKeys, context, cancellationToken)
                .ConfigureAwait(false);
            return (wrote, []);
        }

        var genTypes = config.SshGenKeyTypes is { Count: > 0 }
            ? config.SshGenKeyTypes
            : DefaultGenKeyTypes;
        var deleteExisting = config.SshDeleteKeys == true;

        logger.LogInformation(
            "Generating ssh host keys [{Types}] (deleteExisting={Delete}).",
            string.Join(", ", genTypes),
            deleteExisting);

        var fingerprints = await context.Os
            .RegenerateSshHostKeysAsync(genTypes, deleteExisting, cancellationToken)
            .ConfigureAwait(false);

        return (true, fingerprints);
    }

    /// <summary>
    /// Writes the operator-supplied <c>ssh_keys</c> flat dict. Keys look like
    /// <c>rsa_private</c> / <c>rsa_public</c> / <c>ecdsa_private</c> / … —
    /// paired by the <c>&lt;type&gt;</c> prefix. A private without a matching
    /// public is tolerated (public line written as null); <c>dsa</c> is warned
    /// and skipped. Returns true if at least one key was written.
    /// </summary>
    private async Task<bool> WriteSuppliedHostKeysAsync(
        IReadOnlyDictionary<string, string> sshKeys,
        IModuleContext context,
        CancellationToken cancellationToken)
    {
        // Group the flat dict into <type> -> (private?, public?).
        var byType = new Dictionary<string, (string? Private, string? Public)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in sshKeys)
        {
            if (string.IsNullOrWhiteSpace(key))
                continue;

            var lastUnderscore = key.LastIndexOf('_');
            if (lastUnderscore <= 0 || lastUnderscore == key.Length - 1)
                continue;

            var type = key[..lastUnderscore];
            var kind = key[(lastUnderscore + 1)..];
            var entry = byType.GetValueOrDefault(type);
            if (string.Equals(kind, "private", StringComparison.OrdinalIgnoreCase))
                byType[type] = (value, entry.Public);
            else if (string.Equals(kind, "public", StringComparison.OrdinalIgnoreCase))
                byType[type] = (entry.Private, value);
            // Other suffixes (e.g. the rarely-used certificate forms) are ignored.
        }

        var wroteAny = false;
        foreach (var (type, pair) in byType)
        {
            if (string.Equals(type, "dsa", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning("Skipping supplied dsa host key — DSA was removed in OpenSSH 9.8.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(pair.Private))
            {
                logger.LogWarning("Skipping supplied '{Type}' host key — no private key present.", type);
                continue;
            }

            logger.LogInformation("Writing operator-supplied '{Type}' host key.", type);
            await context.Os.WriteSshHostKeyAsync(
                type,
                pair.Private,
                string.IsNullOrWhiteSpace(pair.Public) ? null : pair.Public,
                cancellationToken).ConfigureAwait(false);
            wroteAny = true;
        }

        return wroteAny;
    }

    /// <summary>
    /// Builds and writes the <c>50-eryph.conf</c> drop-in. Returns true if a
    /// drop-in with at least one directive was written.
    /// </summary>
    private async Task<bool> ProcessDropInAsync(
        CloudConfigModel config,
        IModuleContext context,
        CancellationToken cancellationToken)
    {
        // Always ensure the Include is present so the drop-in is actually read.
        await context.Os.EnsureSshdConfigIncludeAsync(cancellationToken).ConfigureAwait(false);

        var sb = new StringBuilder();

        // ssh_pwauth three-state: bool? null == "unchanged" (omit directive).
        if (config.SshPwauth == true)
            sb.Append("PasswordAuthentication yes\n");
        else if (config.SshPwauth == false)
            sb.Append("PasswordAuthentication no\n");

        // Public-key auth is always enabled — that is how the agent-delivered
        // authorized_keys are honoured.
        sb.Append("PubkeyAuthentication yes\n");

        // disable_root -> deny the built-in (RID-500) Administrator, resolved by
        // SID so a rename survives. SEPARATE from the provisioning default user.
        if (config.DisableRoot == true)
        {
            var admin = await context.Os.ResolveBuiltinAdministratorNameAsync(cancellationToken)
                .ConfigureAwait(false);
            sb.Append("DenyUsers ").Append(admin).Append('\n');
        }

        // PubkeyAuthentication is always emitted, so the drop-in always carries
        // at least one directive; keep the guard for clarity / future tuning.
        if (sb.Length == 0)
        {
            logger.LogDebug("No sshd_config directives to write; skipping drop-in.");
            return false;
        }

        logger.LogInformation("Writing sshd_config drop-in '{File}'.", DropInFileName);
        await context.Os.WriteSshdDropInAsync(DropInFileName, sb.ToString(), cancellationToken)
            .ConfigureAwait(false);
        return true;
    }

    private async Task ProcessAuthorizedKeysAsync(
        CloudConfigModel config,
        IModuleContext context,
        CancellationToken cancellationToken)
    {
        await ProcessTopLevelKeysAsync(config, context, cancellationToken).ConfigureAwait(false);
        await ProcessPerUserKeysAsync(config, context, cancellationToken).ConfigureAwait(false);
    }

    private async Task ProcessTopLevelKeysAsync(
        CloudConfigModel config,
        IModuleContext context,
        CancellationToken cancellationToken)
    {
        // cloud-init applies the datasource's get_public_ssh_keys() to the
        // default user, merged with cloud-config ssh_authorized_keys. Combine
        // both; SetUserSshAuthorizedKeysAsync already merges + dedups.
        var keys = new List<string>();
        if (config.SshAuthorizedKeys is { Count: > 0 })
            keys.AddRange(config.SshAuthorizedKeys);
        if (context.DataSource.SshPublicKeys is { Count: > 0 })
            keys.AddRange(context.DataSource.SshPublicKeys);

        if (keys.Count == 0)
            return;

        // Top-level keys target the provisioning default user (cloud-init's
        // system_info.default_user). Resolved via IDefaultUserResolver, NOT a
        // hardcoded "Administrator".
        var target = defaultUser.Resolve(config, context.DataSource);
        logger.LogInformation(
            "Writing {Count} top-level ssh authorized key(s) for '{User}'.",
            keys.Count,
            target);

        try
        {
            await context.Os.SetUserSshAuthorizedKeysAsync(target, keys, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A single authorized_keys failure must not abort the rest of the
            // module (cloud-init continues with the next target).
            logger.LogError(ex, "Failed to write top-level authorized_keys for '{User}'.", target);
        }
    }

    private async Task ProcessPerUserKeysAsync(
        CloudConfigModel config,
        IModuleContext context,
        CancellationToken cancellationToken)
    {
        if (config.Users is null)
            return;

        foreach (var user in config.Users)
        {
            if (string.IsNullOrWhiteSpace(user.Name) || user.SshAuthorizedKeys is null || user.SshAuthorizedKeys.Count == 0)
                continue;

            logger.LogInformation(
                "Writing {Count} ssh authorized key(s) for '{User}'.",
                user.SshAuthorizedKeys.Count,
                user.Name);

            try
            {
                await context.Os.SetUserSshAuthorizedKeysAsync(user.Name, user.SshAuthorizedKeys, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to write authorized_keys for '{User}'; continuing.", user.Name);
            }
        }
    }
}
