using System.Security.Claims;
using AwesomeAssertions;
using Eryph.GuestServices.Client;
using Eryph.GuestServices.Sockets;
using Microsoft.DevTunnels.Ssh;
using Microsoft.DevTunnels.Ssh.Algorithms;
using Microsoft.DevTunnels.Ssh.Tcp;

namespace Eryph.GuestServices.DevTunnels.Ssh.Extensions.Tests;

// Drives the PRODUCTION client path (GuestSsh.EstablishSessionAsync with
// enablePortForwarding) against a real SSH server, then has the server — standing in
// for an untrusted guest — try to open channels back to the client. The engine
// integration tests use SshTestHelper's own client and so bypass GuestSsh's
// pre-connect channel-type reject; this covers exactly that gap for channel types
// that no service handles. (A guest-initiated direct-tcpip is handled by the
// PortForwardingService pin and is covered by
// ProxyTunnelEngineTests.Guest_cannot_open_a_channel_back_to_a_host_port.)
[Collection("e2e")]
public class GuestSshSecurityTests
{
    [Theory]
    [InlineData("session")]  // an unrelated channel type the client must never service
    [InlineData("x11")]
    [InlineData("auth-agent@openssh.com")]
    public async Task Guest_initiated_channel_is_refused(string channelType)
    {
        using var hardTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var helper = new SshTestHelper();
        await helper.SetupAsync(typeof(PortForwardingService));

        // SetupAsync already authenticated its own throwaway client, so the next
        // authenticating session is the production client we build below.
        var serverSession = new TaskCompletionSource<SshServerSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        helper.Server!.SessionAuthenticating += (s, e) =>
        {
            if (s is SshServerSession ss) serverSession.TrySetResult(ss);
            e.AuthenticationTask = Task.FromResult<ClaimsPrincipal?>(new ClaimsPrincipal());
        };

        var clientSocket = await SocketFactory.CreateClientSocket(HyperVAddresses.Loopback, helper.ServiceId);
        await using var clientStream = new System.Net.Sockets.NetworkStream(clientSocket, ownsSocket: true);
        var keyPair = SshAlgorithms.PublicKey.ECDsaSha2Nistp256.GenerateKeyPair();

        using var session = await GuestSsh.EstablishSessionAsync(
            clientStream, keyPair, keepAliveTimeoutSeconds: 0, enablePortForwarding: true, cancellation: hardTimeout.Token);

        var guest = await serverSession.Task.WaitAsync(hardTimeout.Token);

        var open = async () => await guest.OpenChannelAsync(channelType, hardTimeout.Token);
        await open.Should().ThrowAsync<SshChannelException>(
            "the untrusted guest must not be able to open a {0} channel back to the client", channelType);
    }
}
