using System.Security.Cryptography;
using Eryph.GuestServices.Provisioning.Stages;
using Microsoft.Extensions.Logging;
using CloudConfigModel = Eryph.GuestServices.CloudConfig.CloudConfig;

namespace Eryph.GuestServices.Provisioning.Handlers;

// Ordering note: this handler runs at Stage.Users Order=1, *after* UsersGroupsHandler
// (Order=0). If a user is named in both `users[].passwd`/`plain_text_passwd` and in
// `chpasswd.users`, the chpasswd entry takes effect because it runs later and
// overwrites the password set by UsersGroupsHandler. This matches cloud-init.
[Stage(Stage.Users, Order = 1)]
internal sealed class SetPasswordsHandler(ILogger<SetPasswordsHandler> logger) : IHandler
{
    private const string RandomType = "RANDOM";
    private const int RandomPasswordLength = 16;

    // 16 chars from a 70-glyph alphabet -> ~98 bits of entropy, plenty for a
    // first-boot bootstrap password the host operator will harvest out-of-band.
    // The generated value must never be written to logs.
    private const string PasswordAlphabet =
        "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789!@#$%^&*-_=+";

    public async Task<HandlerOutcome> ApplyAsync(
        CloudConfigModel config,
        IHandlerContext context,
        CancellationToken cancellationToken)
    {
        await ProcessChpasswdUsersAsync(config, context, cancellationToken).ConfigureAwait(false);
        await ProcessChpasswdListAsync(config, context, cancellationToken).ConfigureAwait(false);
        await ProcessPasswordShorthandAsync(config, context, cancellationToken).ConfigureAwait(false);

        return HandlerOutcome.Ok();
    }

    private async Task ProcessChpasswdUsersAsync(
        CloudConfigModel config,
        IHandlerContext context,
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
                // TODO(C-fix): once IHostStatusReporter exposes a secret-reporting
                // channel (e.g. ReportGeneratedCredentialAsync), pipe `password`
                // through it here so the orchestrator can retrieve it.
                logger.LogInformation("Generated random password for '{User}'.", entry.Name);
            }

            if (string.IsNullOrEmpty(password))
            {
                logger.LogWarning("chpasswd entry for '{User}' has no password and is not RANDOM; skipping.", entry.Name);
                continue;
            }

            await SetPasswordAsync(context, entry.Name, password, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ProcessChpasswdListAsync(
        CloudConfigModel config,
        IHandlerContext context,
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

            await SetPasswordAsync(context, user, password, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ProcessPasswordShorthandAsync(
        CloudConfigModel config,
        IHandlerContext context,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(config.Password))
            return;

        var user = config.Users?.FirstOrDefault(u => !string.IsNullOrWhiteSpace(u.Name))?.Name
            ?? "Administrator";

        await SetPasswordAsync(context, user, config.Password, cancellationToken).ConfigureAwait(false);
    }

    private async Task SetPasswordAsync(
        IHandlerContext context,
        string user,
        string password,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Setting password for '{User}'.", user);
        await context.Os.SetLocalUserPasswordAsync(user, password, mustChangeAtNextLogon: false, cancellationToken)
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
