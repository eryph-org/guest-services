using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Claims;
using Eryph.GuestServices.Sockets;
using Microsoft.DevTunnels.Ssh;

namespace Eryph.GuestServices.DevTunnels.Ssh.Extensions.Tests;

public class SshTestHelper : IDisposable
{
    private readonly List<IDisposable> _disposables = new();
    
    public SocketSshServer Server { get; private set; } = null!;
    public SshClientSession ClientSession { get; private set; } = null!;
    public Guid ServiceId { get; private set; }

    public async Task<SshTestHelper> SetupAsync(params Type[] services)
    {
        var portNumber = Random.Shared.Next(42400, 42500);
        ServiceId = PortNumberConverter.ToIntegrationId((uint)portNumber);
        
        var serverKeyPair = SshAlgorithms.PublicKey.ECDsaSha2Nistp256.GenerateKeyPair();
        var clientKeyPair = SshAlgorithms.PublicKey.ECDsaSha2Nistp256.GenerateKeyPair();

        var config = new SshSessionConfiguration();
        foreach (var service in services)
        {
            config.Services.Add(service, null);
        }

        Server = new SocketSshServer(config, new TraceSource("Server"));
        Server.Credentials = new SshServerCredentials(serverKeyPair);
        
        var serverSocket = await SocketFactory.CreateServerSocket(ListenMode.Loopback, ServiceId, 1);
        _disposables.Add(serverSocket);
        
        Server.SessionAuthenticating += (sender, e) => e.AuthenticationTask = Task.FromResult<ClaimsPrincipal?>(new ClaimsPrincipal());
        Server.ExceptionRaised += (_, ex) => throw new Exception("Exception in SSH server", ex);
        _ = Server.AcceptSessionsAsync(serverSocket);

        var clientSocket = await SocketFactory.CreateClientSocket(HyperVAddresses.Loopback, ServiceId);
        _disposables.Add(clientSocket);
        
        var clientStream = new NetworkStream(clientSocket, true);
        _disposables.Add(clientStream);
        
        ClientSession = new SshClientSession(config, new TraceSource("Client"));
        ClientSession.Authenticating += (s, e) => e.AuthenticationTask = Task.FromResult<ClaimsPrincipal?>(new ClaimsPrincipal());

        await ClientSession.ConnectAsync(clientStream);
        var isAuthenticated = await ClientSession.AuthenticateAsync(new SshClientCredentials("egs-test", clientKeyPair));
        
        if (!isAuthenticated)
            throw new InvalidOperationException("Failed to authenticate SSH session");

        return this;
    }

    public void Dispose()
    {
        ClientSession?.Dispose();
        Server?.Dispose();
        
        foreach (var disposable in _disposables)
        {
            disposable?.Dispose();
        }
        
        _disposables.Clear();
    }
}