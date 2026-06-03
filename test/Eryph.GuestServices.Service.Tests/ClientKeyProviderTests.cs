using AwesomeAssertions;
using Eryph.GuestServices.Core;
using Eryph.GuestServices.HvDataExchange.Guest;
using Eryph.GuestServices.Service.Services;
using Microsoft.DevTunnels.Ssh;
using Microsoft.DevTunnels.Ssh.Algorithms;
using Microsoft.DevTunnels.Ssh.Keys;
using Microsoft.Extensions.Logging.Abstractions;

namespace Eryph.GuestServices.Service.Tests;

public class ClientKeyProviderTests
{
    [Fact]
    public async Task IsAuthorizedAsync_ProvisionedKeyMatchesCandidate_ReturnsTrue()
    {
        var provisioned = GenerateKey();
        var provider = NewProvider(provisionedKey: provisioned);

        (await provider.IsAuthorizedAsync(provisioned)).Should().BeTrue();
    }

    [Fact]
    public async Task IsAuthorizedAsync_NoProvisionedKey_KvpSingleKeyMatches_ReturnsTrue()
    {
        var kvpKey = GenerateKey();
        var provider = NewProvider(kvpValue: ExportSsh(kvpKey));

        (await provider.IsAuthorizedAsync(kvpKey)).Should().BeTrue();
    }

    [Fact]
    public async Task IsAuthorizedAsync_ProvisionedAndKvpAreDifferentKeys_BothAuthorize()
    {
        // Side-by-side: the provisioned (geneset) key keeps working AND any
        // extra key added through KVP (add-ssh-config / future multi-host)
        // also works. The whole point of this PR.
        var provisioned = GenerateKey();
        var kvpKey = GenerateKey();
        var provider = NewProvider(provisionedKey: provisioned, kvpValue: ExportSsh(kvpKey));

        (await provider.IsAuthorizedAsync(provisioned)).Should().BeTrue();
        (await provider.IsAuthorizedAsync(kvpKey)).Should().BeTrue();
    }

    [Fact]
    public async Task IsAuthorizedAsync_NeitherSourceMatches_ReturnsFalse()
    {
        var provisioned = GenerateKey();
        var kvpKey = GenerateKey();
        var stranger = GenerateKey();
        var provider = NewProvider(provisionedKey: provisioned, kvpValue: ExportSsh(kvpKey));

        (await provider.IsAuthorizedAsync(stranger)).Should().BeFalse();
    }

    [Fact]
    public async Task IsAuthorizedAsync_KvpEntryMissing_NoProvisionedKey_ReturnsFalse()
    {
        var stranger = GenerateKey();
        var provider = NewProvider();

        (await provider.IsAuthorizedAsync(stranger)).Should().BeFalse();
    }

    [Fact]
    public async Task IsAuthorizedAsync_KvpValueWhitespaceOnly_TreatedAsEmpty()
    {
        var stranger = GenerateKey();
        var provider = NewProvider(kvpValue: "   \r\n\t  ");

        (await provider.IsAuthorizedAsync(stranger)).Should().BeFalse();
    }

    [Fact]
    public async Task IsAuthorizedAsync_KvpMultiLineValue_AnyEntryAuthorizes()
    {
        var a = GenerateKey();
        var b = GenerateKey();
        var c = GenerateKey();
        var kvp = string.Join('\n', ExportSsh(a), ExportSsh(b), ExportSsh(c));
        var provider = NewProvider(kvpValue: kvp);

        (await provider.IsAuthorizedAsync(a)).Should().BeTrue();
        (await provider.IsAuthorizedAsync(b)).Should().BeTrue();
        (await provider.IsAuthorizedAsync(c)).Should().BeTrue();
        (await provider.IsAuthorizedAsync(GenerateKey())).Should().BeFalse();
    }

    [Fact]
    public async Task IsAuthorizedAsync_KvpMultiLineCrLf_AnyEntryAuthorizes()
    {
        // KVP transport doesn't normalize line endings; whatever the writer
        // emitted lands here verbatim. Mirror authorized_keys leniency.
        var a = GenerateKey();
        var b = GenerateKey();
        var kvp = ExportSsh(a) + "\r\n" + ExportSsh(b);
        var provider = NewProvider(kvpValue: kvp);

        (await provider.IsAuthorizedAsync(a)).Should().BeTrue();
        (await provider.IsAuthorizedAsync(b)).Should().BeTrue();
    }

    [Fact]
    public async Task IsAuthorizedAsync_KvpBlankAndCommentLinesIgnored_GoodKeyStillAuthorizes()
    {
        var good = GenerateKey();
        var kvp = $"""

                   # this is a comment line
                   {ExportSsh(good)}
                       # indented comment

                   """;
        var provider = NewProvider(kvpValue: kvp);

        (await provider.IsAuthorizedAsync(good)).Should().BeTrue();
    }

    [Fact]
    public async Task IsAuthorizedAsync_MalformedEntryAmongValid_StillAuthorizesValidEntries()
    {
        // One bad line must not lock out the rest of the set.
        var good = GenerateKey();
        var kvp = $"not-a-valid-ssh-key\n{ExportSsh(good)}\nssh-rsa not-base64-garbage";
        var provider = NewProvider(kvpValue: kvp);

        (await provider.IsAuthorizedAsync(good)).Should().BeTrue();
    }

    [Fact]
    public async Task IsAuthorizedAsync_KvpReadFails_ProvisionedKeyStillAuthorizes()
    {
        // Transient KVP failure must not lock out the provisioned identity.
        var provisioned = GenerateKey();
        var provider = NewProvider(
            provisionedKey: provisioned,
            dataExchange: new ThrowingDataExchange());

        (await provider.IsAuthorizedAsync(provisioned)).Should().BeTrue();
    }

    [Fact]
    public async Task IsAuthorizedAsync_KvpReadFails_NoProvisionedKey_DeniesWithoutThrowing()
    {
        var stranger = GenerateKey();
        var provider = NewProvider(dataExchange: new ThrowingDataExchange());

        (await provider.IsAuthorizedAsync(stranger)).Should().BeFalse();
    }

    [Fact]
    public async Task IsAuthorizedAsync_NamedSlotKey_Authorizes()
    {
        // Named slot: eryph:guest-services:client-public-key:laptop-a
        // Single key per slot — the small-payload path that sidesteps the
        // 2 KiB Hyper-V data exchange per-value limit.
        var key = GenerateKey();
        var dataExchange = new StubDataExchange();
        dataExchange.External[$"{Constants.ClientAuthKeyPrefix}laptop-a"] = ExportSsh(key);
        var provider = NewProvider(dataExchange: dataExchange);

        (await provider.IsAuthorizedAsync(key)).Should().BeTrue();
    }

    [Fact]
    public async Task IsAuthorizedAsync_SameKeyInLegacyAndNamedSlot_AuthorizesOnce()
    {
        // add-ssh-config dual-writes for cross-version compatibility: the same
        // key lands in BOTH the legacy slot and the named slot. The reader
        // must not get confused by the duplicate — any matching entry counts.
        var key = GenerateKey();
        var sshKey = ExportSsh(key);
        var dataExchange = new StubDataExchange();
        dataExchange.External[Constants.ClientAuthKey] = sshKey;
        dataExchange.External[$"{Constants.ClientAuthKeyPrefix}laptop-a"] = sshKey;
        var provider = NewProvider(dataExchange: dataExchange);

        (await provider.IsAuthorizedAsync(key)).Should().BeTrue();
        (await provider.IsAuthorizedAsync(GenerateKey())).Should().BeFalse();
    }

    [Fact]
    public async Task IsAuthorizedAsync_LegacySlotAndNamedSlot_BothAuthorizeSideBySide()
    {
        // The legacy single-slot writer (today's add-ssh-config) keeps working
        // even as new named-slot writers add keys without evicting it.
        var legacyKey = GenerateKey();
        var namedKey = GenerateKey();
        var dataExchange = new StubDataExchange();
        dataExchange.External[Constants.ClientAuthKey] = ExportSsh(legacyKey);
        dataExchange.External[$"{Constants.ClientAuthKeyPrefix}ci-runner"] = ExportSsh(namedKey);
        var provider = NewProvider(dataExchange: dataExchange);

        (await provider.IsAuthorizedAsync(legacyKey)).Should().BeTrue();
        (await provider.IsAuthorizedAsync(namedKey)).Should().BeTrue();
    }

    [Fact]
    public async Task IsAuthorizedAsync_MultipleNamedSlots_EachAuthorizes()
    {
        var a = GenerateKey();
        var b = GenerateKey();
        var c = GenerateKey();
        var dataExchange = new StubDataExchange();
        dataExchange.External[$"{Constants.ClientAuthKeyPrefix}laptop-a"] = ExportSsh(a);
        dataExchange.External[$"{Constants.ClientAuthKeyPrefix}laptop-b"] = ExportSsh(b);
        dataExchange.External[$"{Constants.ClientAuthKeyPrefix}ci-runner"] = ExportSsh(c);
        var provider = NewProvider(dataExchange: dataExchange);

        (await provider.IsAuthorizedAsync(a)).Should().BeTrue();
        (await provider.IsAuthorizedAsync(b)).Should().BeTrue();
        (await provider.IsAuthorizedAsync(c)).Should().BeTrue();
        (await provider.IsAuthorizedAsync(GenerateKey())).Should().BeFalse();
    }

    [Fact]
    public async Task IsAuthorizedAsync_NamedSlotEmptyOrWhitespace_IgnoredOtherSlotsAuthorize()
    {
        var good = GenerateKey();
        var dataExchange = new StubDataExchange();
        dataExchange.External[$"{Constants.ClientAuthKeyPrefix}empty"] = "";
        dataExchange.External[$"{Constants.ClientAuthKeyPrefix}blank"] = "   \r\n  ";
        dataExchange.External[$"{Constants.ClientAuthKeyPrefix}good"] = ExportSsh(good);
        var provider = NewProvider(dataExchange: dataExchange);

        (await provider.IsAuthorizedAsync(good)).Should().BeTrue();
    }

    [Fact]
    public async Task IsAuthorizedAsync_MalformedEntryInOneNamedSlot_DoesNotLockOutOtherSlots()
    {
        var good = GenerateKey();
        var dataExchange = new StubDataExchange();
        dataExchange.External[$"{Constants.ClientAuthKeyPrefix}bad"] = "not-a-valid-ssh-key";
        dataExchange.External[$"{Constants.ClientAuthKeyPrefix}good"] = ExportSsh(good);
        var provider = NewProvider(dataExchange: dataExchange);

        (await provider.IsAuthorizedAsync(good)).Should().BeTrue();
    }

    [Fact]
    public async Task IsAuthorizedAsync_UnrelatedExternalKeysIgnored()
    {
        // The External pool also holds shell-override, status, etc. The reader
        // must only consider the client-public-key slot family.
        var legacy = GenerateKey();
        var dataExchange = new StubDataExchange();
        dataExchange.External[Constants.ClientAuthKey] = ExportSsh(legacy);
        dataExchange.External[Constants.ShellKey] = "ssh-ed25519 NOT-A-CLIENT-KEY-just-a-prankster";
        dataExchange.External["some.other.pool.entry"] = ExportSsh(GenerateKey());
        var provider = NewProvider(dataExchange: dataExchange);

        (await provider.IsAuthorizedAsync(legacy)).Should().BeTrue();
        // An unrelated valid key under a foreign KVP slot must not authorize.
        var unrelatedKeyBytes = dataExchange.External["some.other.pool.entry"];
        var unrelatedKey = KeyPair.ImportKey(unrelatedKeyBytes);
        (await provider.IsAuthorizedAsync(unrelatedKey!)).Should().BeFalse();
    }

    [Fact]
    public async Task IsAuthorizedAsync_KvpAuthDisabled_ProvisionedKeyStillAuthorizes()
    {
        // Hardening switch: KVP-delivered keys are rejected, but the on-disk
        // provisioned key (the eryph-zero / geneset identity) remains the
        // single trusted source.
        var provisioned = GenerateKey();
        var dataExchange = new StubDataExchange();
        dataExchange.External[Constants.ClientAuthKey] = ExportSsh(GenerateKey());
        var provider = NewProvider(
            provisionedKey: provisioned,
            dataExchange: dataExchange,
            kvpAuthEnabled: false);

        (await provider.IsAuthorizedAsync(provisioned)).Should().BeTrue();
    }

    [Fact]
    public async Task IsAuthorizedAsync_KvpAuthDisabled_KvpKeysRejected()
    {
        // Even an otherwise-valid KVP entry must not authorize when the
        // operator has disabled KVP-based auth.
        var kvpKey = GenerateKey();
        var dataExchange = new StubDataExchange();
        dataExchange.External[Constants.ClientAuthKey] = ExportSsh(kvpKey);
        var provider = NewProvider(
            dataExchange: dataExchange,
            kvpAuthEnabled: false);

        (await provider.IsAuthorizedAsync(kvpKey)).Should().BeFalse();
    }

    [Fact]
    public async Task IsAuthorizedAsync_KvpAuthDisabled_NamedSlotKeysRejected()
    {
        var namedKey = GenerateKey();
        var dataExchange = new StubDataExchange();
        dataExchange.External[$"{Constants.ClientAuthKeyPrefix}laptop-a"] = ExportSsh(namedKey);
        var provider = NewProvider(
            dataExchange: dataExchange,
            kvpAuthEnabled: false);

        (await provider.IsAuthorizedAsync(namedKey)).Should().BeFalse();
    }

    [Fact]
    public async Task IsAuthorizedAsync_KvpAuthDisabled_NoProvisionedKey_DeniesEverything()
    {
        var dataExchange = new StubDataExchange();
        dataExchange.External[Constants.ClientAuthKey] = ExportSsh(GenerateKey());
        dataExchange.External[$"{Constants.ClientAuthKeyPrefix}laptop-a"] = ExportSsh(GenerateKey());
        var provider = NewProvider(
            dataExchange: dataExchange,
            kvpAuthEnabled: false);

        (await provider.IsAuthorizedAsync(GenerateKey())).Should().BeFalse();
    }

    [Fact]
    public async Task IsAuthorizedAsync_RealWorldAuthorizedKeysFixture_MembersAuthorize()
    {
        // Real-world shape: the fixture has the exact bytes a user would
        // get from `cat ~/.ssh/authorized_keys` — extra trailing whitespace,
        // a stray comment, two different curves with a key comment.
        // We generate two fresh keys and round-trip through the fixture
        // template to keep the test self-contained.
        var ecdsaP256 = SshAlgorithms.PublicKey.ECDsaSha2Nistp256.GenerateKeyPair();
        var ecdsaP384 = SshAlgorithms.PublicKey.ECDsaSha2Nistp384.GenerateKeyPair();
        var fixture =
            "# /home/egs/.ssh/authorized_keys — managed by egs-tool\n"
            + "\n"
            + KeyPair.ExportPublicKey(ecdsaP256, keyFormat: KeyFormat.Ssh) + " admin@workstation\n"
            + "\n"
            + "# alt host\n"
            + KeyPair.ExportPublicKey(ecdsaP384, keyFormat: KeyFormat.Ssh) + " ci@build-runner\r\n";

        var provider = NewProvider(kvpValue: fixture);

        (await provider.IsAuthorizedAsync(ecdsaP256)).Should().BeTrue();
        (await provider.IsAuthorizedAsync(ecdsaP384)).Should().BeTrue();
    }

    [Fact]
    public async Task IsAuthorizedAsync_KeyWithFutureExpiry_Authorizes()
    {
        var key = GenerateKey();
        var line = $"expiry-time=\"{Iso(DateTimeOffset.UtcNow.AddHours(1))}\" {ExportSsh(key)}";
        var provider = NewProvider(kvpValue: line);

        (await provider.IsAuthorizedAsync(key)).Should().BeTrue();
    }

    [Fact]
    public async Task IsAuthorizedAsync_KeyWithPastExpiry_Rejected()
    {
        var key = GenerateKey();
        var line = $"expiry-time=\"{Iso(DateTimeOffset.UtcNow.AddHours(-1))}\" {ExportSsh(key)}";
        var provider = NewProvider(kvpValue: line);

        (await provider.IsAuthorizedAsync(key)).Should().BeFalse();
    }

    [Fact]
    public async Task IsAuthorizedAsync_KeyWithFutureExpiry_OpenSshCompactForm_Authorizes()
    {
        var key = GenerateKey();
        var stamp = DateTimeOffset.UtcNow.AddDays(1).ToString("yyyyMMddHHmmss") + "Z";
        var line = $"expiry-time=\"{stamp}\" {ExportSsh(key)}";
        var provider = NewProvider(kvpValue: line);

        (await provider.IsAuthorizedAsync(key)).Should().BeTrue();
    }

    [Fact]
    public async Task IsAuthorizedAsync_KeyWithPastExpiry_OpenSshCompactForm_Rejected()
    {
        var key = GenerateKey();
        var stamp = DateTimeOffset.UtcNow.AddDays(-1).ToString("yyyyMMddHHmmss") + "Z";
        var line = $"expiry-time=\"{stamp}\" {ExportSsh(key)}";
        var provider = NewProvider(kvpValue: line);

        (await provider.IsAuthorizedAsync(key)).Should().BeFalse();
    }

    [Fact]
    public async Task IsAuthorizedAsync_FutureExpiry_DateOnlyCompactForm_Authorizes()
    {
        // The date-only "yyyyMMdd" compact form is parsed as UTC midnight.
        var key = GenerateKey();
        var stamp = DateTimeOffset.UtcNow.AddDays(2).ToString("yyyyMMdd");
        var line = $"expiry-time=\"{stamp}\" {ExportSsh(key)}";
        var provider = NewProvider(kvpValue: line);

        (await provider.IsAuthorizedAsync(key)).Should().BeTrue();
    }

    [Fact]
    public async Task IsAuthorizedAsync_PastExpiry_DateOnlyCompactForm_Rejected()
    {
        var key = GenerateKey();
        var stamp = DateTimeOffset.UtcNow.AddDays(-2).ToString("yyyyMMdd");
        var line = $"expiry-time=\"{stamp}\" {ExportSsh(key)}";
        var provider = NewProvider(kvpValue: line);

        (await provider.IsAuthorizedAsync(key)).Should().BeFalse();
    }

    [Fact]
    public async Task IsAuthorizedAsync_FutureExpiry_IsoWithNonUtcOffset_NormalizesToUtc()
    {
        // An ISO-8601 timestamp with an explicit offset must be normalized to UTC
        // before comparison. 1 hour from now expressed as -05:00 is still future.
        var key = GenerateKey();
        var stamp = DateTimeOffset.UtcNow.AddHours(1).ToOffset(TimeSpan.FromHours(-5))
            .ToString("yyyy-MM-ddTHH:mm:sszzz");
        var line = $"expiry-time=\"{stamp}\" {ExportSsh(key)}";
        var provider = NewProvider(kvpValue: line);

        (await provider.IsAuthorizedAsync(key)).Should().BeTrue();
    }

    [Fact]
    public async Task IsAuthorizedAsync_PastExpiry_IsoWithNonUtcOffset_NormalizesToUtc()
    {
        var key = GenerateKey();
        var stamp = DateTimeOffset.UtcNow.AddHours(-1).ToOffset(TimeSpan.FromHours(5))
            .ToString("yyyy-MM-ddTHH:mm:sszzz");
        var line = $"expiry-time=\"{stamp}\" {ExportSsh(key)}";
        var provider = NewProvider(kvpValue: line);

        (await provider.IsAuthorizedAsync(key)).Should().BeFalse();
    }

    [Fact]
    public async Task IsAuthorizedAsync_KeyWithMalformedExpiry_Rejected()
    {
        // Fail-closed: a key that declared an expiry we cannot read must not be
        // honored as if it never expired.
        var key = GenerateKey();
        var line = $"expiry-time=\"not-a-timestamp\" {ExportSsh(key)}";
        var provider = NewProvider(kvpValue: line);

        (await provider.IsAuthorizedAsync(key)).Should().BeFalse();
    }

    [Fact]
    public async Task IsAuthorizedAsync_BareExpiryTimeWithoutValue_Rejected()
    {
        // A malformed "expiry-time" with no '=' value must fail closed, not be
        // honored as a never-expiring key.
        var key = GenerateKey();
        var line = $"expiry-time {ExportSsh(key)}";
        var provider = NewProvider(kvpValue: line);

        (await provider.IsAuthorizedAsync(key)).Should().BeFalse();
    }

    [Fact]
    public async Task IsAuthorizedAsync_DifferentOptionStartingWithExpiryTime_Authorizes()
    {
        // An option that merely starts with the same text (e.g. a hypothetical
        // "expiry-time-zone") is a different option, not a malformed expiry, so
        // it must not block the key.
        var key = GenerateKey();
        var line = $"expiry-time-zone=\"utc\" {ExportSsh(key)}";
        var provider = NewProvider(kvpValue: line);

        (await provider.IsAuthorizedAsync(key)).Should().BeTrue();
    }

    [Fact]
    public async Task IsAuthorizedAsync_KeyWithOptionsButNoExpiry_Authorizes()
    {
        // Other authorized_keys options (no expiry-time) must not block the key.
        var key = GenerateKey();
        var line = $"no-port-forwarding,no-agent-forwarding {ExportSsh(key)}";
        var provider = NewProvider(kvpValue: line);

        (await provider.IsAuthorizedAsync(key)).Should().BeTrue();
    }

    [Fact]
    public async Task IsAuthorizedAsync_ExpiryAmongMultipleOptions_FutureAuthorizes()
    {
        var key = GenerateKey();
        var line = $"no-pty,expiry-time=\"{Iso(DateTimeOffset.UtcNow.AddHours(1))}\",no-agent-forwarding {ExportSsh(key)}";
        var provider = NewProvider(kvpValue: line);

        (await provider.IsAuthorizedAsync(key)).Should().BeTrue();
    }

    [Fact]
    public async Task IsAuthorizedAsync_ExpiryAmongMultipleOptions_PastRejected()
    {
        var key = GenerateKey();
        var line = $"no-pty,expiry-time=\"{Iso(DateTimeOffset.UtcNow.AddHours(-1))}\",no-agent-forwarding {ExportSsh(key)}";
        var provider = NewProvider(kvpValue: line);

        (await provider.IsAuthorizedAsync(key)).Should().BeFalse();
    }

    [Fact]
    public async Task IsAuthorizedAsync_OptionWithQuotedSpaces_DoesNotBreakKeyParsing()
    {
        // A quoted option value may contain spaces; the option list still ends
        // at the first UNQUOTED whitespace, so the key body parses correctly.
        var key = GenerateKey();
        var line = $"command=\"echo hello world\",expiry-time=\"{Iso(DateTimeOffset.UtcNow.AddHours(1))}\" {ExportSsh(key)} operator@host";
        var provider = NewProvider(kvpValue: line);

        (await provider.IsAuthorizedAsync(key)).Should().BeTrue();
    }

    [Fact]
    public async Task IsAuthorizedAsync_ExpiredKeyInNamedSlot_DoesNotAuthorize_ButOtherSlotDoes()
    {
        var expired = GenerateKey();
        var live = GenerateKey();
        var dataExchange = new StubDataExchange();
        dataExchange.External[$"{Constants.ClientAuthKeyPrefix}sub-expired"] =
            $"expiry-time=\"{Iso(DateTimeOffset.UtcNow.AddHours(-1))}\" {ExportSsh(expired)}";
        dataExchange.External[$"{Constants.ClientAuthKeyPrefix}sub-live"] =
            $"expiry-time=\"{Iso(DateTimeOffset.UtcNow.AddHours(1))}\" {ExportSsh(live)}";
        var provider = NewProvider(dataExchange: dataExchange);

        (await provider.IsAuthorizedAsync(expired)).Should().BeFalse();
        (await provider.IsAuthorizedAsync(live)).Should().BeTrue();
    }

    private static string Iso(DateTimeOffset value)
        => value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ");

    private static IKeyPair GenerateKey()
        => SshAlgorithms.PublicKey.ECDsaSha2Nistp256.GenerateKeyPair();

    private static string ExportSsh(IKeyPair keyPair)
        => KeyPair.ExportPublicKey(keyPair, keyFormat: KeyFormat.Ssh);

    private static ClientKeyProvider NewProvider(
        IKeyPair? provisionedKey = null,
        string? kvpValue = null,
        IGuestDataExchange? dataExchange = null,
        bool kvpAuthEnabled = true)
    {
        if (dataExchange is null)
        {
            var stub = new StubDataExchange();
            if (kvpValue is not null)
                stub.External[Constants.ClientAuthKey] = kvpValue;
            dataExchange = stub;
        }

        return new ClientKeyProvider(
            new StubKeyStorage(provisionedKey),
            dataExchange,
            new StubFlags(kvpAuthEnabled),
            NullLogger<ClientKeyProvider>.Instance);
    }

    private sealed class StubKeyStorage(IKeyPair? clientKey = null) : IKeyStorage
    {
        public Task<IKeyPair?> GetClientKeyAsync() => Task.FromResult(clientKey);
        public Task<IKeyPair?> GetHostKeyAsync() => throw new InvalidOperationException("not relevant here");
        public Task SetHostKeyAsync(IKeyPair keyPair) => throw new InvalidOperationException("not relevant here");
    }

    private sealed class StubDataExchange : IGuestDataExchange
    {
        public Dictionary<string, string> External { get; } = new();

        public Task<IReadOnlyDictionary<string, string>> GetExternalDataAsync()
            => Task.FromResult<IReadOnlyDictionary<string, string>>(External);

        public Task<IReadOnlyDictionary<string, string>> GetGuestDataAsync()
            => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());

        public Task SetGuestValuesAsync(IReadOnlyDictionary<string, string?> values) => Task.CompletedTask;
    }

    private sealed class ThrowingDataExchange : IGuestDataExchange
    {
        public Task<IReadOnlyDictionary<string, string>> GetExternalDataAsync()
            => throw new InvalidOperationException("simulated KVP failure");

        public Task<IReadOnlyDictionary<string, string>> GetGuestDataAsync()
            => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());

        public Task SetGuestValuesAsync(IReadOnlyDictionary<string, string?> values) => Task.CompletedTask;
    }

    private sealed class StubFlags(bool kvpAuthEnabled = true) : IServiceControlFlags
    {
        public bool IsProvisioningEnabled() => true;
        public bool IsRemoteAccessEnabled() => true;
        public bool IsKvpAuthEnabled() => kvpAuthEnabled;
    }
}
