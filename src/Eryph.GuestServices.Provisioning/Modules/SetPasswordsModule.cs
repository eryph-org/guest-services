using Eryph.GuestServices.Provisioning.Stages;
using Eryph.GuestServices.Provisioning.UserData;
using Microsoft.Extensions.Logging;
using CloudConfigModel = Eryph.GuestServices.CloudConfig.CloudConfig;

namespace Eryph.GuestServices.Provisioning.Modules;

// Ordering note: this module runs at Stage.Config Order=1, *after* UsersGroupsModule
// (Order=0). If a user is named in both `users[].passwd`/`plain_text_passwd` and in
// `chpasswd.users`, the chpasswd entry takes effect because it runs later and
// overwrites the password set by UsersGroupsModule. This matches cloud-init.
//
// Random passwords are NOT supported. cloud-init's `type: RANDOM` (and the list-form
// `R`/`RANDOM` tokens, and a chpasswd entry with no password) generate a password and
// deliver it out-of-band by writing it to /dev/console. Windows guests have no
// equivalent console channel that is reliably captured across the clouds eryph targets,
// so a generated password could never be retrieved — setting one would silently lock the
// operator out. We therefore reject random requests at `egs-tool validate` time and
// warn-and-skip them here at runtime. Explicit passwords, the chpasswd list/users forms
// with known passwords, and `expire` are fully supported.
[Stage(Stage.Config, Order = 1, Frequency = ModuleFrequency.PerInstance)]
internal sealed class SetPasswordsModule(
    ILogger<SetPasswordsModule> logger,
    IDefaultUserResolver defaultUser) : IModule
{
    private const string RandomType = "RANDOM";

    public async Task<ModuleOutcome> ApplyAsync(
        ResolvedUserData userData,
        IModuleContext context,
        CancellationToken cancellationToken)
    {
        var config = userData.CloudConfig;

        // Cloud-init default for `chpasswd.expire` is `true` — every password
        // set through this module should be flagged "must change at next
        // login" unless the operator opts out. The flag flows through all
        // three input forms (users[], list, top-level password shorthand)
        // so cross-cloud cloud-config behaves consistently.
        var expire = config.Chpasswd?.Expire ?? true;

        await ProcessChpasswdUsersAsync(config, context, expire, cancellationToken).ConfigureAwait(false);
        await ProcessChpasswdListAsync(config, context, expire, cancellationToken).ConfigureAwait(false);
        await ProcessPasswordShorthandAsync(config, context, expire, cancellationToken).ConfigureAwait(false);

        return ModuleOutcome.Ok();
    }

    private async Task ProcessChpasswdUsersAsync(
        CloudConfigModel config,
        IModuleContext context,
        bool expire,
        CancellationToken cancellationToken)
    {
        if (config.Chpasswd?.Users is null)
            return;

        foreach (var entry in config.Chpasswd.Users)
        {
            if (string.IsNullOrWhiteSpace(entry.Name))
                continue;

            // cloud-init treats `type: RANDOM` — and a missing password — as
            // "generate a random password". We don't generate (no out-of-band
            // channel on Windows to return it), so warn-and-skip rather than set
            // an unretrievable password the operator can never log in with.
            var isRandomRequest =
                string.Equals(entry.Type, RandomType, StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrEmpty(entry.Password);
            if (isRandomRequest)
            {
                logger.LogWarning(
                    "chpasswd entry for '{User}' requests a random password; random generation is "
                    + "not supported on Windows (no out-of-band channel to return it) — skipping.",
                    entry.Name);
                continue;
            }

            await SetPasswordAsync(context, entry.Name, entry.Password!, expire, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ProcessChpasswdListAsync(
        CloudConfigModel config,
        IModuleContext context,
        bool expire,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(config.Chpasswd?.List))
            return;

        var lines = config.Chpasswd.List.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
                continue;

            // Split only on the FIRST colon so colons inside the password are preserved.
            var colon = trimmed.IndexOf(':');
            if (colon <= 0 || colon == trimmed.Length - 1)
            {
                logger.LogWarning("chpasswd list entry has no 'user:password' shape; ignoring: {Entry}", trimmed);
                continue;
            }

            var user = trimmed[..colon];
            var password = trimmed[(colon + 1)..];

            // cloud-init's cc_set_passwords.py treats the literal tokens `R` and
            // `RANDOM` (exact-case) as "generate a random password". We don't
            // generate (no out-of-band channel on Windows) — warn and skip.
            if (password is "R" or "RANDOM")
            {
                logger.LogWarning(
                    "chpasswd list entry for '{User}' requests a random password (token '{Token}'); "
                    + "random generation is not supported on Windows — skipping.",
                    user, password);
                continue;
            }

            await SetPasswordAsync(context, user, password, expire, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ProcessPasswordShorthandAsync(
        CloudConfigModel config,
        IModuleContext context,
        bool expire,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(config.Password))
            return;

        if (config.Password is "R" or "RANDOM")
        {
            logger.LogWarning(
                "Top-level 'password' requests a random password; random generation is "
                + "not supported on Windows — skipping.");
            return;
        }

        // The top-level `password:` shorthand targets the resolved default user
        // (RFC 0018): the first admin in `users:`, else a datasource- or
        // settings-supplied name, else the "Administrator" fallback. This
        // replaces the previous hardcoded "first user / Administrator" pick so
        // an image-baked default admin or an OpenStack-style metadata name is
        // honoured. `chpasswd.users` / `chpasswd.list` always name an explicit
        // user and are left untouched above.
        var user = defaultUser.Resolve(config, context.DataSource);

        await SetPasswordAsync(context, user, config.Password, expire, cancellationToken).ConfigureAwait(false);
    }

    private async Task SetPasswordAsync(
        IModuleContext context,
        string user,
        string password,
        bool expire,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Setting password for '{User}'.", user);
        await context.Os.SetLocalUserPasswordAsync(user, password, mustChangeAtNextLogon: expire, cancellationToken)
            .ConfigureAwait(false);
    }
}
