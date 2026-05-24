using System.Security.Cryptography;
using Eryph.GuestServices.Provisioning.Stages;
using Eryph.GuestServices.Provisioning.UserData;
using Microsoft.Extensions.Logging;
using CloudConfigModel = Eryph.GuestServices.CloudConfig.CloudConfig;

namespace Eryph.GuestServices.Provisioning.Modules;

// Ordering note: this module runs at Stage.Config Order=1, *after* UsersGroupsModule
// (Order=0). If a user is named in both `users[].passwd`/`plain_text_passwd` and in
// `chpasswd.users`, the chpasswd entry takes effect because it runs later and
// overwrites the password set by UsersGroupsModule. This matches cloud-init.
[Stage(Stage.Config, Order = 1, Frequency = ModuleFrequency.PerInstance)]
internal sealed class SetPasswordsModule(
    ILogger<SetPasswordsModule> logger,
    IDefaultUserResolver defaultUser) : IModule
{
    private const string RandomType = "RANDOM";
    private const int RandomPasswordLength = 16;

    // 16 chars from a 70-glyph alphabet -> ~98 bits of entropy, plenty for a
    // first-boot bootstrap password the host operator will harvest out-of-band.
    // The generated value must never be written to logs.
    private const string PasswordAlphabet =
        "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789!@#$%^&*-_=+";

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

            var password = entry.Password;
            if (string.Equals(entry.Type, RandomType, StringComparison.OrdinalIgnoreCase))
            {
                password = GenerateRandomPassword();
                // SECURITY: do not log the password value. It must never appear in
                // event log, file sinks, or aggregators. The generated value is
                // surfaced to the host out-of-band.
                // TODO(C-fix): once IReportingDispatcher exposes a secret-reporting
                // channel (e.g. a GeneratedCredential event), pipe `password`
                // through it here so the orchestrator can retrieve it.
                logger.LogInformation("Generated random password for '{User}'.", entry.Name);
            }

            if (string.IsNullOrEmpty(password))
            {
                logger.LogWarning("chpasswd entry for '{User}' has no password and is not RANDOM; skipping.", entry.Name);
                continue;
            }

            await SetPasswordAsync(context, entry.Name, password, expire, cancellationToken).ConfigureAwait(false);
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

            // Cloud-init's cc_set_passwords.py treats the literal tokens `R`
            // and `RANDOM` (case-sensitive — the grammar uses an exact-case
            // match) as "generate a random password for this user". Mirror
            // that: any other value (including mixed-case `Random` or
            // `random`) is the literal password.
            if (password is "R" or "RANDOM")
            {
                password = GenerateRandomPassword();
                logger.LogInformation("Generated random password for '{User}' (chpasswd list).", user);
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

    private static string GenerateRandomPassword()
    {
        // Rejection sampling to avoid modulo bias: discard any byte that would
        // map non-uniformly across the alphabet. Bytes in [0, threshold) map
        // uniformly via `% alphabet.Length`.
        var alphabet = PasswordAlphabet;
        var threshold = 256 - (256 % alphabet.Length);
        var chars = new char[RandomPasswordLength];
        Span<byte> one = stackalloc byte[1];
        for (var i = 0; i < chars.Length; i++)
        {
            byte b;
            do
            {
                RandomNumberGenerator.Fill(one);
                b = one[0];
            } while (b >= threshold);
            chars[i] = alphabet[b % alphabet.Length];
        }

        return new string(chars);
    }
}
