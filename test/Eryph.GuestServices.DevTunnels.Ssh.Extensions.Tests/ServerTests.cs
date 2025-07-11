using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.Versioning;
using Eryph.GuestServices.Sockets;
using Microsoft.DevTunnels.Ssh;

namespace Eryph.GuestServices.DevTunnels.Ssh.Extensions.Tests;

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

        var server = new SocketSshServer(config, new TraceSource("Server"));
        server.Credentials = new SshServerCredentials(serverKeyPair);
        var serverSocket = await SocketFactory.CreateServerSocket(ListenMode.Loopback, serviceId, 1);
        _ = server.AcceptSessionsAsync(serverSocket);


        var clientSocket = await SocketFactory.CreateClientSocket(HyperVAddresses.Loopback, serviceId);
        await using var clientStream = new NetworkStream(clientSocket, true);
        var clientSession = new SshClientSession(config, new TraceSource("Client"));
        await clientSession.ConnectAsync(clientStream);

        await clientSession.AuthenticateAsync(new SshClientCredentials("egs-test", clientKeyPair));
    }
}
