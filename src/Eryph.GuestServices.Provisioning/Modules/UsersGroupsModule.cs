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

            var spec = new LocalUserSpec
            {
                Name = user.Name,
                Comment = null,
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

    private static bool IsSudoEnabled(string? sudo)
    {
        if (sudo is null)
            return false;

        var trimmed = sudo.Trim();
        if (trimmed.Length == 0)
            return false;

        // cloud-init treats anything other than "false" (case-insensitive) as
        // "this user gets sudo". On Windows that means Administrators.
        return !string.Equals(trimmed, "false", StringComparison.OrdinalIgnoreCase);
    }
}
