using AwesomeAssertions;
using Eryph.GuestServices.Core;
using Eryph.GuestServices.DevTunnels.Ssh.Extensions;
using Eryph.GuestServices.HvDataExchange.Guest;
using Eryph.GuestServices.Service.Services;
using Microsoft.DevTunnels.Ssh.Algorithms;
using Microsoft.Extensions.Logging.Abstractions;

namespace Eryph.GuestServices.Service.Tests;

public class SshServerServiceRemoteAccessGateTests
{
    [Fact]
    public async Task StartAsync_RemoteAccessDisabled_DoesNotTouchDependenciesOrBind()
    {
        // Every dependency throws if touched. The disabled path must short-circuit
        // before reading a host key or binding the vsock listener, so none of
        // them is invoked.
        var service = new SshServerService(
            new ThrowingKeyStorage(),
            new ThrowingHostKeyGenerator(),
            new ThrowingDataExchange(),
            new ThrowingClientKeyProvider(),
            new ThrowingShellSelector(),
            new FixedFlags(remoteAccessEnabled: false),
            NullLogger<SshServerService>.Instance);

        var start = async () => await service.StartAsync(CancellationToken.None);

        await start.Should().NotThrowAsync();
    }

    [Fact]
    public async Task StopAndDispose_WhenNeverStarted_AreSafe()
    {
        var service = new SshServerService(
            new ThrowingKeyStorage(),
            new ThrowingHostKeyGenerator(),
            new ThrowingDataExchange(),
            new ThrowingClientKeyProvider(),
            new ThrowingShellSelector(),
            new FixedFlags(remoteAccessEnabled: false),
            NullLogger<SshServerService>.Instance);

        await service.StartAsync(CancellationToken.None);

        // Neither teardown path must observe the (never-created) listener/socket
        // nor write KVP status (which would touch the throwing data exchange).
        var stop = async () => await service.StopAsync(CancellationToken.None);
        var dispose = async () => await service.DisposeAsync();

        await stop.Should().NotThrowAsync();
        await dispose.Should().NotThrowAsync();
    }

    private sealed class FixedFlags(bool remoteAccessEnabled) : IServiceControlFlags
    {
        public bool IsProvisioningEnabled() => true;
        public bool IsRemoteAccessEnabled() => remoteAccessEnabled;
        public bool IsKvpAuthEnabled() => true;
        public bool IsAutoUpdateEnabled() => false;
        public bool IsPortForwardingEnabled() => false;
    }

    private sealed class ThrowingKeyStorage : IKeyStorage
    {
        public Task<IKeyPair?> GetClientKeyAsync() => throw new InvalidOperationException("should not be called");
        public Task<IKeyPair?> GetHostKeyAsync() => throw new InvalidOperationException("should not be called");
        public Task SetHostKeyAsync(IKeyPair keyPair) => throw new InvalidOperationException("should not be called");
    }

    private sealed class ThrowingHostKeyGenerator : IHostKeyGenerator
    {
        public IKeyPair GenerateHostKey() => throw new InvalidOperationException("should not be called");
    }

    private sealed class ThrowingClientKeyProvider : IClientKeyProvider
    {
        public Task<bool> IsAuthorizedAsync(IKeyPair candidate) => throw new InvalidOperationException("should not be called");
    }

    private sealed class ThrowingShellSelector : IShellSelector
    {
        public Task<ShellSelection> SelectAsync(ShellOverride sshOverride, CancellationToken cancellation)
            => throw new InvalidOperationException("should not be called");
    }

    private sealed class ThrowingDataExchange : IGuestDataExchange
    {
        public Task<IReadOnlyDictionary<string, string>> GetExternalDataAsync()
            => throw new InvalidOperationException("should not be called");
        public Task<IReadOnlyDictionary<string, string>> GetGuestDataAsync()
            => throw new InvalidOperationException("should not be called");
        public Task SetGuestValuesAsync(IReadOnlyDictionary<string, string?> values)
            => throw new InvalidOperationException("should not be called");
    }
}
