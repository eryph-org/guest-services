using Eryph.GuestServices.Provisioning.Stages;
using Eryph.GuestServices.Provisioning.UserData;
using Eryph.GuestServices.Provisioning.Windows;
using Microsoft.Extensions.Logging;
using CloudConfigModel = Eryph.GuestServices.CloudConfig.CloudConfig;

namespace Eryph.GuestServices.Provisioning.Modules;

[Stage(Stage.Config, Order = 0, Frequency = ModuleFrequency.PerInstance)]
internal sealed class UsersGroupsModule(ILogger<UsersGroupsModule> logger) : IModule
{
    public async Task<ModuleOutcome> ApplyAsync(
        ResolvedUserData userData,
        IModuleContext context,
        CancellationToken cancellationToken)
    {
        var config = userData.CloudConfig;
        await ProcessGroupsAsync(config, context.Os, cancellationToken).ConfigureAwait(false);
        await ProcessUsersAsync(config, context.Os, cancellationToken).ConfigureAwait(false);

        return ModuleOutcome.Ok();
    }

    private async Task ProcessGroupsAsync(CloudConfigModel config, IWindowsOs os, CancellationToken cancellationToken)
    {
        if (config.Groups is null)
            return;

        foreach (var group in config.Groups)
        {
            if (string.IsNullOrWhiteSpace(group.Name))
            {
                logger.LogWarning("Skipping group entry with empty name.");
                continue;
            }

            if (!await os.LocalGroupExistsAsync(group.Name, cancellationToken).ConfigureAwait(false))
            {
                logger.LogInformation("Creating local group '{Group}'.", group.Name);
                await os.CreateLocalGroupAsync(group.Name, cancellationToken).ConfigureAwait(false);
            }

            if (group.Members is null)
                continue;

            foreach (var member in group.Members)
            {
                if (string.IsNullOrWhiteSpace(member))
                    continue;
                logger.LogInformation("Adding '{Member}' to group '{Group}'.", member, group.Name);
                await os.AddUserToGroupAsync(member, group.Name, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task ProcessUsersAsync(CloudConfigModel config, IWindowsOs os, CancellationToken cancellationToken)
    {
        if (config.Users is null)
            return;

        foreach (var user in config.Users)
        {
            if (string.IsNullOrWhiteSpace(user.Name))
            {
                logger.LogWarning("Skipping user entry with empty name.");
                continue;
            }

            // Cloud-init's GECOS field on Linux populates /etc/passwd's
            // comment column AND is widely treated as "full name". The
            // closest Windows analogue is the NetUserInfo2.usri2_full_name
            // (visible in lusrmgr.msc as "Full name"); we mirror to both
            // Comment and FullName so a fresh user record gets the right
            // display value and subsequent updates also sync FullName.
            var spec = new LocalUserSpec
            {
                Name = user.Name,
                Comment = user.Gecos,
                FullName = user.Gecos,
                HomeDir = user.HomeDir,
                Disabled = user.LockPasswd,
            };

            var exists = await os.LocalUserExistsAsync(user.Name, cancellationToken).ConfigureAwait(false);
            if (!exists)
            {
                logger.LogInformation("Creating local user '{User}'.", user.Name);
                await os.CreateLocalUserAsync(spec, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                logger.LogInformation("Updating local user '{User}'.", user.Name);
                await os.UpdateLocalUserAsync(spec, cancellationToken).ConfigureAwait(false);
            }

            // PlainTextPasswd is the cloud-init alias that explicitly carries a
            // plaintext password (vs. Passwd, which may carry a hashed value).
            // On Windows we cannot apply hashes, so we treat both identically;
            // PlainTextPasswd wins if both are set, mirroring cloud-init.
            var passwordToSet =
                !string.IsNullOrEmpty(user.PlainTextPasswd) ? user.PlainTextPasswd :
                !string.IsNullOrEmpty(user.Passwd) ? user.Passwd :
                null;
            if (passwordToSet is not null)
            {
                logger.LogInformation("Setting password for '{User}'.", user.Name);
                await os.SetLocalUserPasswordAsync(user.Name, passwordToSet, mustChangeAtNextLogon: false, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (user.Groups is not null)
            {
                foreach (var group in user.Groups)
                {
                    if (string.IsNullOrWhiteSpace(group))
                        continue;
                    if (!await os.LocalGroupExistsAsync(group, cancellationToken).ConfigureAwait(false))
                    {
                        logger.LogInformation("Creating local group '{Group}' (referenced by user '{User}').", group, user.Name);
                        await os.CreateLocalGroupAsync(group, cancellationToken).ConfigureAwait(false);
                    }

                    logger.LogInformation("Adding '{User}' to group '{Group}'.", user.Name, group);
                    await os.AddUserToGroupAsync(user.Name, group, cancellationToken).ConfigureAwait(false);
                }
            }

            if (IsSudoEnabled(user.Sudo))
            {
                logger.LogInformation("Ensuring '{User}' is in the local Administrators group.", user.Name);
                await os.EnsureUserInAdministratorsAsync(user.Name, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Cloud-init's <c>sudo</c> is a string-or-list union; the schema carries
    /// it as <c>IReadOnlyList&lt;string&gt;?</c>. This Windows-side shim
    /// collapses the list to the binary "is this user an Administrator"
    /// answer because there is no Windows equivalent of per-rule sudoers
    /// semantics (NOPASSWD, runas restrictions, command lists).
    /// <para>
    /// Decision rule (locked by tests): the user is promoted to
    /// Administrators if at least one non-empty entry exists that is not the
    /// literal string <c>"false"</c> (case-insensitive, trimmed). An entry of
    /// <c>"false"</c> mixed with other entries does NOT veto promotion — any
    /// non-false entry wins. Empty / null / list-of-only-"false" → no
    /// promotion. Per-rule sudoers semantics are platform-irrelevant on
    /// Windows and intentionally not modeled.
    /// </para>
    /// </summary>
    private static bool IsSudoEnabled(IReadOnlyList<string>? sudo)
    {
        if (sudo is null || sudo.Count == 0)
            return false;

        foreach (var entry in sudo)
        {
            if (string.IsNullOrWhiteSpace(entry))
                continue;
            var trimmed = entry.Trim();
            // cloud-init treats anything other than "false" (case-insensitive) as
            // "this user gets sudo". On Windows that means Administrators.
            if (!string.Equals(trimmed, "false", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
