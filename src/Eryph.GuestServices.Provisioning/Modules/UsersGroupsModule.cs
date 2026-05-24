using Eryph.GuestServices.Provisioning.Configuration;
using Eryph.GuestServices.Provisioning.Stages;
using Eryph.GuestServices.Provisioning.UserData;
using Eryph.GuestServices.Provisioning.Windows;
using Microsoft.Extensions.Logging;
using CloudConfigModel = Eryph.GuestServices.CloudConfig.CloudConfig;

namespace Eryph.GuestServices.Provisioning.Modules;

[Stage(Stage.Config, Order = 0, Frequency = ModuleFrequency.PerInstance)]
internal sealed class UsersGroupsModule(
    ILogger<UsersGroupsModule> logger,
    ProvisioningSettings settings,
    IDefaultUserResolver defaultUser) : IModule
{
    // The fallback group set when settings.DefaultUser.Groups is null. An
    // auto-created default user is an administrator by intent (it is the
    // account credential shorthands target), so it lands in Administrators.
    private static readonly IReadOnlyList<string> DefaultUserGroups = ["Administrators"];

    public async Task<ModuleOutcome> ApplyAsync(
        ResolvedUserData userData,
        IModuleContext context,
        CancellationToken cancellationToken)
    {
        var config = userData.CloudConfig;
        await ProcessGroupsAsync(config, context.Os, cancellationToken).ConfigureAwait(false);
        await ProcessUsersAsync(config, context.Os, cancellationToken).ConfigureAwait(false);
        await EnsureDefaultUserAsync(config, context, cancellationToken).ConfigureAwait(false);

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

            if (SudoPolicy.IsSudoEnabled(user.Sudo))
            {
                logger.LogInformation("Ensuring '{User}' is in the local Administrators group.", user.Name);
                await os.EnsureUserInAdministratorsAsync(user.Name, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    // Auto-create the image-baked default user (RFC 0018) when the operator
    // opts in via settings.DefaultUser.CreateIfMissing. This enables the
    // OpenStack-style flow where a password-only cloud-config provisions a
    // known admin account that the `users:` block never declares. Runs after
    // the explicit `users:` processing so the later SetPasswords / Ssh modules
    // (Order 1 / 2) can target an account that now exists. Without
    // CreateIfMissing this is a no-op and behaviour is exactly as before.
    private async Task EnsureDefaultUserAsync(
        CloudConfigModel config,
        IModuleContext context,
        CancellationToken cancellationToken)
    {
        if (!settings.DefaultUser.CreateIfMissing)
            return;

        var os = context.Os;
        var name = defaultUser.Resolve(config, context.DataSource);

        // Already declared in `users:` (and thus processed above) — don't
        // duplicate. Match cloud-init's case-insensitive account semantics on
        // Windows.
        var declared = config.Users?.Any(u =>
            string.Equals(u.Name, name, StringComparison.OrdinalIgnoreCase)) ?? false;
        if (declared)
            return;

        if (await os.LocalUserExistsAsync(name, cancellationToken).ConfigureAwait(false))
            return;

        logger.LogInformation("Auto-creating default user '{User}' (DefaultUser.CreateIfMissing).", name);
        await os.CreateLocalUserAsync(new LocalUserSpec { Name = name }, cancellationToken).ConfigureAwait(false);

        var groups = settings.DefaultUser.Groups ?? DefaultUserGroups;
        foreach (var group in groups)
        {
            if (string.IsNullOrWhiteSpace(group))
                continue;

            // "Administrators" is promoted via the SID-correct helper so a
            // localized / renamed builtin group is matched by RID-544, not by
            // its display name. Other groups use the by-name path, creating
            // the group on the fly like the per-user logic above.
            if (string.Equals(group, "Administrators", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogInformation("Ensuring default user '{User}' is in the local Administrators group.", name);
                await os.EnsureUserInAdministratorsAsync(name, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (!await os.LocalGroupExistsAsync(group, cancellationToken).ConfigureAwait(false))
            {
                logger.LogInformation("Creating local group '{Group}' (referenced by default user '{User}').", group, name);
                await os.CreateLocalGroupAsync(group, cancellationToken).ConfigureAwait(false);
            }

            logger.LogInformation("Adding default user '{User}' to group '{Group}'.", name, group);
            await os.AddUserToGroupAsync(name, group, cancellationToken).ConfigureAwait(false);
        }
    }
}
