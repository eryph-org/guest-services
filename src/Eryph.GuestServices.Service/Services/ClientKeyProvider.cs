using System.Globalization;
using Eryph.GuestServices.Core;
using Eryph.GuestServices.HvDataExchange.Guest;
using Microsoft.DevTunnels.Ssh;
using Microsoft.DevTunnels.Ssh.Algorithms;
using Microsoft.Extensions.Logging;

namespace Eryph.GuestServices.Service.Services;

public class ClientKeyProvider : IClientKeyProvider
{
    private readonly IKeyStorage _keyStorage;
    private readonly IGuestDataExchange _dataExchange;
    private readonly ILogger<ClientKeyProvider> _logger;

    // Read the KVP-auth flag once at construction and cache it. Matches the
    // existing IsRemoteAccessEnabled pattern in SshServerService.StartAsync:
    // operator changes to the KvpAuthEnabled flag (HKLM\SOFTWARE\eryph\
    // guest-services on Windows, /etc/opt/eryph/guest-services/
    // service-control.conf on Linux) take effect on the next service
    // restart, not on the next auth attempt. Hot-reload would surprise
    // operators expecting deterministic policy and also make audit trails
    // harder ("which value was in force at the time of this auth?").
    private readonly bool _kvpAuthEnabled;

    public ClientKeyProvider(
        IKeyStorage keyStorage,
        IGuestDataExchange dataExchange,
        IServiceControlFlags controlFlags,
        ILogger<ClientKeyProvider> logger)
    {
        _keyStorage = keyStorage;
        _dataExchange = dataExchange;
        _logger = logger;
        _kvpAuthEnabled = controlFlags.IsKvpAuthEnabled();
        if (!_kvpAuthEnabled)
        {
            _logger.LogInformation(
                "KVP-delivered authorized client keys are disabled (KvpAuthEnabled=0); only the locally provisioned key will authorize. Restart the service after changing the flag.");
        }
    }

    public async Task<bool> IsAuthorizedAsync(IKeyPair candidate)
    {
        var candidateBytes = candidate.GetPublicKeyBytes();

        // The locally provisioned key (geneset / cloud-init writes it to disk
        // during catlet creation) is the catlet's primary authorized identity.
        // It survives KVP being cleared and is the only source on older
        // catlets that predate the KVP delivery channel.
        var provisionedKey = await _keyStorage.GetClientKeyAsync();
        if (provisionedKey is not null && provisionedKey.GetPublicKeyBytes() == candidateBytes)
            return true;

        // Hardening switch (cached at startup; see ctor). When KVP-delivered
        // keys are disabled the provisioned key above is the only source of
        // trust — nothing pushed at runtime via add-ssh-config (or any future
        // writer) can authorize against a catlet until the service is restarted
        // with the flag flipped back.
        if (!_kvpAuthEnabled)
            return false;

        // KVP carries additional keys pushed at runtime (egs-tool
        // add-ssh-config and future multi-host scenarios). Re-read on every
        // auth attempt so rotation and adds take effect without restarting
        // the guest or clearing any cache.
        IReadOnlyDictionary<string, string> guestData;
        try
        {
            guestData = await _dataExchange.GetExternalDataAsync();
        }
        catch (Exception ex)
        {
            // KVP reads can fail transiently. Don't lock the user out of the
            // provisioned key just because the data exchange is unreachable;
            // the cached/provisioned branch above already returned its verdict.
            _logger.LogInformation(ex, "Failed to read external KVP data while evaluating authorized client keys.");
            return false;
        }

        foreach (var (kvpKey, kvpValue) in guestData)
        {
            // Accept the legacy single slot AND any named slot under the
            // ":<id>" prefix. The per-slot value still parses as
            // authorized_keys-style 1..N lines so a writer can pack a few
            // related keys without spinning up more slots.
            if (kvpKey != Constants.ClientAuthKey
                && !kvpKey.StartsWith(Constants.ClientAuthKeyPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(kvpValue))
                continue;

            foreach (var line in ParseAuthorizedKeyLines(kvpValue))
            {
                var keyPair = TryImportAuthorizedKey(line);
                if (keyPair is null)
                    continue;

                if (keyPair.GetPublicKeyBytes() == candidateBytes)
                    return true;
            }
        }

        return false;
    }

    // authorized_keys-style: one OpenSSH public key per line, blank lines and
    // '#' comments ignored. A single-line value (today's add-ssh-config wire
    // shape) is a degenerate one-entry list — same code path, no special case.
    private static IEnumerable<string> ParseAuthorizedKeyLines(string raw)
    {
        using var reader = new StringReader(raw);
        while (reader.ReadLine() is { } line)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] == '#')
                continue;
            yield return trimmed;
        }
    }

    // Parse one authorized_keys line of the form
    //   [options ]keytype base64 [comment]
    // honoring a leading 'expiry-time="<timestamp>"' option. A line whose
    // expiry-time is in the past is rejected (returns null) and so cannot
    // authorize, even if its key bytes match the candidate. Lines without
    // options parse exactly as before. Returns null for anything unparseable
    // so one bad entry never locks out the rest of the set.
    private IKeyPair? TryImportAuthorizedKey(string line)
    {
        try
        {
            var (options, keyBody) = SplitOptions(line);

            if (options is not null && IsExpired(options))
            {
                // IsExpired is fail-closed: it rejects both a past expiry and a
                // malformed/unparseable expiry-time, so the message covers both.
                _logger.LogInformation(
                    "Skipping authorized client key entry: its expiry-time has passed or could not be parsed.");
                return null;
            }

            return KeyPair.ImportKey(keyBody);
        }
        catch (Exception ex)
        {
            // One malformed entry must not lock out the rest of the set.
            _logger.LogInformation(ex, "Skipping unparseable authorized client key entry.");
            return null;
        }
    }

    // Splits an authorized_keys line into its (optional) leading option list
    // and the remaining "keytype base64 [comment]" key body. Returns
    // (null, line) when there are no options. The option list ends at the
    // first unquoted whitespace; quoted strings (double quotes, with '\"'
    // escapes) may contain spaces and commas. Detection is heuristic and
    // matches OpenSSH: the first token is treated as an option list only when
    // it is not itself a known key type (e.g. "ssh-ed25519", "ecdsa-..."),
    // i.e. only when an unquoted space appears before a recognizable key body.
    private static (string? options, string keyBody) SplitOptions(string line)
    {
        // A bare key body starts with a key type token. If the first token
        // looks like a key type, there are no options. Otherwise the leading
        // run up to the first unquoted whitespace is the option list.
        var firstSpace = IndexOfUnquotedWhitespace(line, 0);
        if (firstSpace < 0)
        {
            // Single token: no options, just hand it to the importer.
            return (null, line);
        }

        var firstToken = line[..firstSpace];
        if (LooksLikeKeyType(firstToken))
            return (null, line);

        // The first token contained option syntax (e.g. an '=' or a quoted
        // value) or simply is not a key type: treat it as the option list.
        var keyBody = line[(firstSpace + 1)..].TrimStart();
        return (firstToken, keyBody);
    }

    // True when the token is an OpenSSH public-key type. Key types contain no
    // '=' and no quotes (which option lists use) and follow the well-known
    // prefixes. Being permissive here is safe: a non-key-type first token is
    // simply treated as options, and the subsequent ImportKey still validates.
    private static bool LooksLikeKeyType(string token)
    {
        if (token.Contains('=') || token.Contains('"'))
            return false;

        return token.StartsWith("ssh-", StringComparison.Ordinal)
            || token.StartsWith("ecdsa-", StringComparison.Ordinal)
            || token.StartsWith("sk-", StringComparison.Ordinal)
            || token.StartsWith("rsa-sha2-", StringComparison.Ordinal);
    }

    // Index of the first whitespace character that is not inside a double-quoted
    // segment, scanning from <start>. Returns -1 if none. Honors '\"' escapes
    // inside quotes, matching OpenSSH option parsing.
    private static int IndexOfUnquotedWhitespace(string s, int start)
    {
        var inQuotes = false;
        for (var i = start; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '\\' && inQuotes && i + 1 < s.Length)
            {
                i++; // skip escaped char
                continue;
            }
            if (c == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }
            if (!inQuotes && char.IsWhiteSpace(c))
                return i;
        }
        return -1;
    }

    // Evaluates the comma-separated option list; returns true when an
    // 'expiry-time="<timestamp>"' option is present and the timestamp is in the
    // past. A malformed or unparseable expiry-time is treated as expired
    // (fail-closed): a key that asked to expire but whose timestamp we cannot
    // read must not be honored as if it never expired.
    private static bool IsExpired(string options)
    {
        foreach (var option in SplitOptionList(options))
        {
            const string name = "expiry-time";
            if (!option.StartsWith(name, StringComparison.OrdinalIgnoreCase))
                continue;

            var rest = option[name.Length..].TrimStart();
            if (rest.Length == 0 || rest[0] != '=')
                continue; // a different option that merely starts with the same text

            var rawValue = rest[1..].Trim();
            // Strip surrounding quotes if present.
            if (rawValue.Length >= 2 && rawValue[0] == '"' && rawValue[^1] == '"')
                rawValue = rawValue[1..^1];

            if (!TryParseExpiry(rawValue, out var expiry))
                return true; // fail-closed on unparseable expiry

            return expiry <= DateTimeOffset.UtcNow;
        }

        return false;
    }

    // Splits an option list on commas that are not inside double-quoted values.
    private static IEnumerable<string> SplitOptionList(string options)
    {
        var start = 0;
        var inQuotes = false;
        for (var i = 0; i < options.Length; i++)
        {
            var c = options[i];
            if (c == '\\' && inQuotes && i + 1 < options.Length)
            {
                i++;
                continue;
            }
            if (c == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }
            if (c == ',' && !inQuotes)
            {
                yield return options[start..i].Trim();
                start = i + 1;
            }
        }
        if (start <= options.Length)
            yield return options[start..].Trim();
    }

    // Tolerant parser for the expiry-time value. Accepts:
    //   - OpenSSH "YYYYMMDD" and "YYYYMMDDHHMM[SS]" with an optional trailing 'Z'
    //     (UTC when 'Z' present or when no offset is given, matching OpenSSH which
    //     interprets these in the system time zone but we standardize on UTC for a
    //     deterministic, host-time-zone-independent verdict in the guest);
    //   - ISO-8601 / RFC3339 timestamps (e.g. 2026-06-02T14:30:00Z, with offset).
    // Returns the instant as a DateTimeOffset (UTC) on success.
    private static bool TryParseExpiry(string value, out DateTimeOffset expiry)
    {
        expiry = default;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        value = value.Trim();

        // OpenSSH compact forms: all-digits, optionally suffixed with 'Z'.
        var hasZ = value.EndsWith("Z", StringComparison.OrdinalIgnoreCase);
        var digits = hasZ ? value[..^1] : value;
        if (digits.Length > 0 && digits.All(char.IsDigit))
        {
            string[] compactFormats =
            [
                "yyyyMMdd",
                "yyyyMMddHHmm",
                "yyyyMMddHHmmss",
            ];

            if (DateTime.TryParseExact(
                    digits,
                    compactFormats,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var compact))
            {
                expiry = new DateTimeOffset(compact, TimeSpan.Zero);
                return true;
            }

            return false;
        }

        // ISO-8601 / RFC3339. Treat a value with no explicit offset as UTC so the
        // guest's local time zone never changes the verdict.
        if (DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out expiry))
        {
            return true;
        }

        return false;
    }
}
