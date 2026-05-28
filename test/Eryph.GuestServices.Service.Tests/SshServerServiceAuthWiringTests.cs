using AwesomeAssertions;
using Eryph.GuestServices.Core;
using Eryph.GuestServices.DevTunnels.Ssh.Extensions;
using Eryph.GuestServices.HvDataExchange.Guest;
using Eryph.GuestServices.Service.Services;
using Microsoft.DevTunnels.Ssh;
using Microsoft.DevTunnels.Ssh.Algorithms;
using Microsoft.Extensions.Logging.Abstractions;

namespace Eryph.GuestServices.Service.Tests;

/// <summary>
/// Guards the small but easy-to-break wiring inside
/// <see cref="SshServerService.CheckClientKey"/>: the candidate must reach the
/// provider verbatim, and the authorization boolean must not be inverted.
/// </summary>
public class SshServerServiceAuthWiringTests
{
    [Fact]
    public async Task CheckClientKey_PlumbsCandidateAndReturnsPrincipalWhenAuthorized()
    {
        var candidate = SshAlgorithms.PublicKey.ECDsaSha2Nistp256.GenerateKeyPair();
        var provider = new RecordingClientKeyProvider(authorize: true);
        var service = NewService(provider);

        var principal = await service.CheckClientKey(candidate);

        principal.Should().NotBeNull();
        provider.Calls.Should().ContainSingle().Which.Should().BeSameAs(candidate);
    }

    [Fact]
    public async Task CheckClientKey_ReturnsNullWhenProviderDenies()
    {
        var candidate = SshAlgorithms.PublicKey.ECDsaSha2Nistp256.GenerateKeyPair();
        var provider = new RecordingClientKeyProvider(authorize: false);
        var service = NewService(provider);

        var principal = await service.CheckClientKey(candidate);

        principal.Should().BeNull();
    }

    private static SshServerService NewService(IClientKeyProvider provider) => new(
        new NoopKeyStorage(),
        new NoopHostKeyGenerator(),
        new NoopDataExchange(),
        provider,
        new NoopShellSelector(),
        new EnabledFlags(),
        NullLogger<SshServerService>.Instance);

    private sealed class RecordingClientKeyProvider(bool authorize) : IClientKeyProvider
    {
        public List<IKeyPair> Calls { get; } = new();

        public Task<bool> IsAuthorizedAsync(IKeyPair candidate)
        {
            Calls.Add(candidate);
            return Task.FromResult(authorize);
        }
    }

    private sealed class NoopKeyStorage : IKeyStorage
    {
        public Task<IKeyPair?> GetClientKeyAsync() => Task.FromResult<IKeyPair?>(null);
        public Task<IKeyPair?> GetHostKeyAsync() => Task.FromResult<IKeyPair?>(null);
        public Task SetHostKeyAsync(IKeyPair keyPair) => Task.CompletedTask;
    }

    private sealed class NoopHostKeyGenerator : IHostKeyGenerator
    {
        public IKeyPair GenerateHostKey()
            => SshAlgorithms.PublicKey.ECDsaSha2Nistp256.GenerateKeyPair();
    }

    private sealed class NoopDataExchange : IGuestDataExchange
    {
        public Task<IReadOnlyDictionary<string, string>> GetExternalDataAsync()
            => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());

        public Task<IReadOnlyDictionary<string, string>> GetGuestDataAsync()
            => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());

        public Task SetGuestValuesAsync(IReadOnlyDictionary<string, string?> values) => Task.CompletedTask;
    }

    private sealed class NoopShellSelector : IShellSelector
    {
        public Task<ShellSelection> SelectAsync(ShellOverride sshOverride, CancellationToken cancellation)
            => Task.FromResult(new ShellSelection("/bin/sh", string.Empty));
    }

    private sealed class EnabledFlags : IServiceControlFlags
    {
        public bool IsProvisioningEnabled() => true;
        public bool IsRemoteAccessEnabled() => true;
        public bool IsKvpAuthEnabled() => true;
    }
}
