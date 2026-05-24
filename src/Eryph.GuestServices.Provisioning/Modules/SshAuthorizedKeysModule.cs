using Eryph.GuestServices.Provisioning.Stages;
using Eryph.GuestServices.Provisioning.UserData;
using Microsoft.Extensions.Logging;
using CloudConfigModel = Eryph.GuestServices.CloudConfig.CloudConfig;

namespace Eryph.GuestServices.Provisioning.Modules;

[Stage(Stage.Config, Order = 2, Frequency = ModuleFrequency.PerInstance)]
internal sealed class SshAuthorizedKeysModule(ILogger<SshAuthorizedKeysModule> logger) : IModule
{
    public async Task<ModuleOutcome> ApplyAsync(
        ResolvedUserData userData,
        IModuleContext context,
        CancellationToken cancellationToken)
    {
        var config = userData.CloudConfig;
        await ProcessTopLevelKeysAsync(config, context, cancellationToken).ConfigureAwait(false);
        await ProcessPerUserKeysAsync(config, context, cancellationToken).ConfigureAwait(false);

        return ModuleOutcome.Ok();
    }

    private async Task ProcessTopLevelKeysAsync(
        CloudConfigModel config,
        IModuleContext context,
        CancellationToken cancellationToken)
    {
        if (config.SshAuthorizedKeys is null || config.SshAuthorizedKeys.Count == 0)
            return;

        // Top-level keys go to the Administrator account by default. If a
        // sudo-enabled user is configured, prefer them.
        var target = PickAdminUser(config);
        logger.LogInformation(
            "Writing {Count} top-level ssh authorized key(s) for '{User}'.",
            config.SshAuthorizedKeys.Count,
            target);

        await context.Os.SetUserSshAuthorizedKeysAsync(target, config.SshAuthorizedKeys, cancellationToken)
            .ConfigureAwait(false);
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

            await context.Os.SetUserSshAuthorizedKeysAsync(user.Name, user.SshAuthorizedKeys, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static string PickAdminUser(CloudConfigModel config)
    {
        // Cloud-init's `sudo` is a string-or-list union. Treat any entry that
        // isn't explicitly "false" as the truthy signal — same compile-shim
        // policy as UsersGroupsModule.IsSudoEnabled. Phase 3 wires the
        // full per-entry sudoers semantics; on Windows we collapse the list
        // back to the binary "is this user an admin" answer.
        var sudoUser = config.Users?.FirstOrDefault(u =>
            !string.IsNullOrWhiteSpace(u.Name)
            && HasTruthySudoEntry(u.Sudo));
        return sudoUser?.Name ?? "Administrator";
    }

    private static bool HasTruthySudoEntry(IReadOnlyList<string>? sudo)
    {
        if (sudo is null || sudo.Count == 0)
            return false;
        foreach (var entry in sudo)
        {
            if (string.IsNullOrWhiteSpace(entry))
                continue;
            if (!string.Equals(entry.Trim(), "false", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
