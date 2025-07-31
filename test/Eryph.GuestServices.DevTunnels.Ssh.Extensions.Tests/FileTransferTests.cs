using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Claims;
using AwesomeAssertions;
using Eryph.GuestServices.Sockets;
using Eryph.GuestServices.DevTunnels.Ssh.Extensions.Services;
using Microsoft.DevTunnels.Ssh;

namespace Eryph.GuestServices.DevTunnels.Ssh.Extensions.Tests;

[Collection("e2e")]
public sealed class FileTransferTests : IDisposable
{
    private readonly string _path;

    public FileTransferTests()
    {
        _path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_path);
    }

    [Fact]
    public async Task Test()
    {
        var srcPath = Path.Combine(_path, "src.bin");
        await File.WriteAllTextAsync(srcPath, "Hello World!");
        var targetPath = Path.Combine(_path, "target.bin");

        // The loopback connection works without actually registering the Hyper-V integration.
        var serviceId = PortNumberConverter.ToIntegrationId(42424);

        var serverKeyPair = SshAlgorithms.PublicKey.ECDsaSha2Nistp256.GenerateKeyPair();
        var clientKeyPair = SshAlgorithms.PublicKey.ECDsaSha2Nistp256.GenerateKeyPair();

        var config = new SshSessionConfiguration();
        config.Services.Add(typeof(UploadFileService), null);

        using var server = new SocketSshServer(config, new TraceSource("Server"));
        server.Credentials = new SshServerCredentials(serverKeyPair);
        using var serverSocket = await SocketFactory.CreateServerSocket(ListenMode.Loopback, serviceId, 1);
        server.SessionAuthenticating += (sender, e) =>   e.AuthenticationTask = Task.FromResult<ClaimsPrincipal?>(new ClaimsPrincipal());
        server.ExceptionRaised += (_, ex) => throw new Exception("Exception in SSH server", ex);
        _ = server.AcceptSessionsAsync(serverSocket);


        using var clientSocket = await SocketFactory.CreateClientSocket(HyperVAddresses.Loopback, serviceId);
        await using var clientStream = new NetworkStream(clientSocket, true);
        using var clientSession = new SshClientSession(config, new TraceSource("Client"));
        clientSession.Authenticating += (s, e) => e.AuthenticationTask = Task.FromResult<ClaimsPrincipal?>(new ClaimsPrincipal());

        await clientSession.ConnectAsync(clientStream);
        var isAuthenticated = await clientSession.AuthenticateAsync(new SshClientCredentials("egs-test", clientKeyPair));
        isAuthenticated.Should().BeTrue();

        await using (var srcStream = new FileStream(srcPath, FileMode.Open, FileAccess.Read))
        {
            await clientSession.TransferFileAsync(targetPath, "", srcStream, false, CancellationToken.None);
        }

        var targetContent = await File.ReadAllTextAsync(targetPath);
        targetContent.Should().Be("Hello World!");
    }

    public void Dispose()
    {
        Directory.Delete(_path, true);
    }
}
