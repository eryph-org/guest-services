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
    // operator changes to HKLM\SOFTWARE\eryph\guest-services\KvpAuthEnabled
    // take effect on the next service restart, not on the next auth attempt.
    // Hot-reload would surprise operators expecting deterministic policy and
    // also make audit trails harder ("which value was in force at the time
    // of this auth?").
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
                var keyPair = TryImport(line);
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

    private IKeyPair? TryImport(string line)
    {
        try
        {
            return KeyPair.ImportKey(line);
        }
        catch (Exception ex)
        {
            // One malformed entry must not lock out the rest of the set.
            _logger.LogInformation(ex, "Skipping unparseable authorized client key entry.");
            return null;
        }
    }
}
