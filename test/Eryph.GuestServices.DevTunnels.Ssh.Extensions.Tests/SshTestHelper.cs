using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net.Sockets;
using System.Security.Claims;
using Eryph.GuestServices.Sockets;
using Microsoft.DevTunnels.Ssh;

namespace Eryph.GuestServices.DevTunnels.Ssh.Extensions.Tests;

public sealed class SshTestHelper : IDisposable
{
    private Socket? _clientSocket;
    private Stream? _clientStream;
    private Socket? _serverSocket;

    public SshTestHelper()
    {
        // Use fixed port to avoid test flakiness
        var portNumber = 42425u;
        ServiceId = PortNumberConverter.ToIntegrationId(portNumber);
    }

    public SshClientSession? ClientSession { get; private set; }
    
    public SocketSshServer? Server { get; private set; }

    public Guid ServiceId { get; init; }

    [MemberNotNull(nameof(ClientSession), nameof(Server))]
    public async Task SetupAsync(params Type[] services)
    {
        var serverKeyPair = SshAlgorithms.PublicKey.ECDsaSha2Nistp256.GenerateKeyPair();
        var clientKeyPair = SshAlgorithms.PublicKey.ECDsaSha2Nistp256.GenerateKeyPair();

        var config = new SshSessionConfiguration();
        foreach (var service in services)
        {
            config.Services.Add(service, null);
        }

        Server = new SocketSshServer(config, new TraceSource("Server"));
        Server.Credentials = new SshServerCredentials(serverKeyPair);
        
        _serverSocket = await SocketFactory.CreateServerSocket(ListenMode.Loopback, ServiceId, 1);
        
        Server.SessionAuthenticating += (_, e) => e.AuthenticationTask = Task.FromResult<ClaimsPrincipal?>(new ClaimsPrincipal());
        Server.ExceptionRaised += (_, ex) => throw new Exception("Exception in SSH server", ex);
        _ = Server.AcceptSessionsAsync(_serverSocket);

        _clientSocket = await SocketFactory.CreateClientSocket(HyperVAddresses.Loopback, ServiceId);
        
        _clientStream = new NetworkStream(_clientSocket, true);
        
        ClientSession = new SshClientSession(config, new TraceSource("Client"));
        ClientSession.Authenticating += (s, e) => e.AuthenticationTask = Task.FromResult<ClaimsPrincipal?>(new ClaimsPrincipal());

        await ClientSession.ConnectAsync(_clientStream);
        var isAuthenticated = await ClientSession.AuthenticateAsync(new SshClientCredentials("egs-test", clientKeyPair));
        
        if (!isAuthenticated)
            throw new InvalidOperationException("Failed to authenticate SSH session");
    }

    public void Dispose()
    {
        _clientStream?.Dispose();
        _clientSocket?.Dispose();
        _serverSocket?.Dispose();

        ClientSession?.Dispose();
        Server?.Dispose();
    }
}