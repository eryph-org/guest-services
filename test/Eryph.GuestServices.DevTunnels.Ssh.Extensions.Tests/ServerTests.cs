using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Claims;
using AwesomeAssertions;
using Eryph.GuestServices.Sockets;
using Microsoft.DevTunnels.Ssh;
using Microsoft.DevTunnels.Ssh.Events;

namespace Eryph.GuestServices.DevTunnels.Ssh.Extensions.Tests;

[Collection("e2e")]
public class ServerTests
{
    [Fact]
    public async Task CanConnect()
    {
        // The loopback connection works without actually registering the Hyper-V integration.
        var serviceId = PortNumberConverter.ToIntegrationId(42424);
        
        var serverKeyPair = SshAlgorithms.PublicKey.ECDsaSha2Nistp256.GenerateKeyPair();
        var clientKeyPair = SshAlgorithms.PublicKey.ECDsaSha2Nistp256.GenerateKeyPair();

        var config = new SshSessionConfiguration();

        using var server = new SocketSshServer(config, new TraceSource("Server"));
        server.SessionAuthenticating += (sender, e) =>
        {
            if (e.AuthenticationType is not (SshAuthenticationType.ClientPublicKey or SshAuthenticationType.ClientPublicKeyQuery))
                return;
            
            if (e.PublicKey?.GetPublicKeyBytes() != clientKeyPair.GetPublicKeyBytes())
                return;

            e.AuthenticationTask = Task.FromResult<ClaimsPrincipal?>(new ClaimsPrincipal());
        };
        
        server.Credentials = new SshServerCredentials(serverKeyPair);
        using var serverSocket = await SocketFactory.CreateServerSocket(ListenMode.Loopback, serviceId, 1);
        _ = server.AcceptSessionsAsync(serverSocket);


        using var clientSocket = await SocketFactory.CreateClientSocket(HyperVAddresses.Loopback, serviceId);
        await using var clientStream = new NetworkStream(clientSocket, true);
        var clientSession = new SshClientSession(config, new TraceSource("Client"));
        clientSession.Authenticating += (_, e) =>
        {
            if (e.AuthenticationType != SshAuthenticationType.ServerPublicKey)
                return;

            if (e.PublicKey?.GetPublicKeyBytes() != serverKeyPair.GetPublicKeyBytes())
                return;

            e.AuthenticationTask = Task.FromResult<ClaimsPrincipal?>(new ClaimsPrincipal());
        };
        await clientSession.ConnectAsync(clientStream);

        var isAuthenticated = await clientSession.AuthenticateAsync(new SshClientCredentials("egs-test", clientKeyPair));
        isAuthenticated.Should().BeTrue();
    }
}
