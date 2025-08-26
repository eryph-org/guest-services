using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Claims;
using AwesomeAssertions;
using Eryph.GuestServices.Sockets;
using Eryph.GuestServices.DevTunnels.Ssh.Extensions.Services;
using Microsoft.DevTunnels.Ssh;

namespace Eryph.GuestServices.DevTunnels.Ssh.Extensions.Tests;

[Collection("e2e")]
public sealed class FileDownloadTests : IDisposable
{
    private readonly string _path;

    public FileDownloadTests()
    {
        _path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_path);
    }

    [Fact]
    public async Task DownloadFile_ShouldTransferFileFromServerToClient()
    {
        var srcPath = Path.Combine(_path, "src.bin");
        await File.WriteAllTextAsync(srcPath, "Hello Download World!");
        var targetPath = Path.Combine(_path, "target.bin");

        // The loopback connection works without actually registering the Hyper-V integration.
        var serviceId = PortNumberConverter.ToIntegrationId(42425); // Different port from upload tests

        var serverKeyPair = SshAlgorithms.PublicKey.ECDsaSha2Nistp256.GenerateKeyPair();
        var clientKeyPair = SshAlgorithms.PublicKey.ECDsaSha2Nistp256.GenerateKeyPair();

        var config = new SshSessionConfiguration();
        config.Services.Add(typeof(DownloadFileService), null);

        using var server = new SocketSshServer(config, new TraceSource("Server"));
        server.Credentials = new SshServerCredentials(serverKeyPair);
        using var serverSocket = await SocketFactory.CreateServerSocket(ListenMode.Loopback, serviceId, 1);
        server.SessionAuthenticating += (sender, e) => e.AuthenticationTask = Task.FromResult<ClaimsPrincipal?>(new ClaimsPrincipal());
        server.ExceptionRaised += (_, ex) => throw new Exception("Exception in SSH server", ex);
        _ = server.AcceptSessionsAsync(serverSocket);

        using var clientSocket = await SocketFactory.CreateClientSocket(HyperVAddresses.Loopback, serviceId);
        await using var clientStream = new NetworkStream(clientSocket, true);
        using var clientSession = new SshClientSession(config, new TraceSource("Client"));
        clientSession.Authenticating += (s, e) => e.AuthenticationTask = Task.FromResult<ClaimsPrincipal?>(new ClaimsPrincipal());

        await clientSession.ConnectAsync(clientStream);
        var isAuthenticated = await clientSession.AuthenticateAsync(new SshClientCredentials("egs-test", clientKeyPair));
        isAuthenticated.Should().BeTrue();

        await using (var targetStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write))
        {
            await clientSession.DownloadFileAsync(srcPath, "", targetStream, CancellationToken.None);
        }

        var targetContent = await File.ReadAllTextAsync(targetPath);
        targetContent.Should().Be("Hello Download World!");
    }

    [Fact]
    public async Task DownloadFile_ShouldReturnFileNotFoundError_WhenSourceFileDoesNotExist()
    {
        var nonExistentPath = Path.Combine(_path, "nonexistent.bin");
        var targetPath = Path.Combine(_path, "target.bin");

        // The loopback connection works without actually registering the Hyper-V integration.
        var serviceId = PortNumberConverter.ToIntegrationId(42426); // Different port

        var serverKeyPair = SshAlgorithms.PublicKey.ECDsaSha2Nistp256.GenerateKeyPair();
        var clientKeyPair = SshAlgorithms.PublicKey.ECDsaSha2Nistp256.GenerateKeyPair();

        var config = new SshSessionConfiguration();
        config.Services.Add(typeof(DownloadFileService), null);

        using var server = new SocketSshServer(config, new TraceSource("Server"));
        server.Credentials = new SshServerCredentials(serverKeyPair);
        using var serverSocket = await SocketFactory.CreateServerSocket(ListenMode.Loopback, serviceId, 1);
        server.SessionAuthenticating += (sender, e) => e.AuthenticationTask = Task.FromResult<ClaimsPrincipal?>(new ClaimsPrincipal());
        server.ExceptionRaised += (_, ex) => throw new Exception("Exception in SSH server", ex);
        _ = server.AcceptSessionsAsync(serverSocket);

        using var clientSocket = await SocketFactory.CreateClientSocket(HyperVAddresses.Loopback, serviceId);
        await using var clientStream = new NetworkStream(clientSocket, true);
        using var clientSession = new SshClientSession(config, new TraceSource("Client"));
        clientSession.Authenticating += (s, e) => e.AuthenticationTask = Task.FromResult<ClaimsPrincipal?>(new ClaimsPrincipal());

        await clientSession.ConnectAsync(clientStream);
        var isAuthenticated = await clientSession.AuthenticateAsync(new SshClientCredentials("egs-test", clientKeyPair));
        isAuthenticated.Should().BeTrue();

        await using var targetStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write);
        
        var result = await clientSession.DownloadFileAsync(nonExistentPath, "", targetStream, CancellationToken.None);
        result.Should().Be(ErrorCodes.FileNotFound);
    }

    [Fact]
    public async Task ListDirectory_ShouldReturnDirectoryContents()
    {
        // Create test directory structure
        var testDir = Path.Combine(_path, "testdir");
        Directory.CreateDirectory(testDir);
        
        // Create files in the directory
        await File.WriteAllTextAsync(Path.Combine(testDir, "file1.txt"), "Content 1");
        await File.WriteAllTextAsync(Path.Combine(testDir, "file2.txt"), "Content 2");
        
        // Create subdirectory
        var subDir = Path.Combine(testDir, "subdir");
        Directory.CreateDirectory(subDir);
        await File.WriteAllTextAsync(Path.Combine(subDir, "file3.txt"), "Content 3");

        var serviceId = PortNumberConverter.ToIntegrationId(42427);
        var serverKeyPair = SshAlgorithms.PublicKey.ECDsaSha2Nistp256.GenerateKeyPair();
        var clientKeyPair = SshAlgorithms.PublicKey.ECDsaSha2Nistp256.GenerateKeyPair();

        var config = new SshSessionConfiguration();
        config.Services.Add(typeof(ListDirectoryService), null);

        using var server = new SocketSshServer(config, new TraceSource("Server"));
        server.Credentials = new SshServerCredentials(serverKeyPair);
        using var serverSocket = await SocketFactory.CreateServerSocket(ListenMode.Loopback, serviceId, 1);
        server.SessionAuthenticating += (sender, e) => e.AuthenticationTask = Task.FromResult<ClaimsPrincipal?>(new ClaimsPrincipal());
        server.ExceptionRaised += (_, ex) => throw new Exception("Exception in SSH server", ex);
        _ = server.AcceptSessionsAsync(serverSocket);

        using var clientSocket = await SocketFactory.CreateClientSocket(HyperVAddresses.Loopback, serviceId);
        await using var clientStream = new NetworkStream(clientSocket, true);
        using var clientSession = new SshClientSession(config, new TraceSource("Client"));
        clientSession.Authenticating += (s, e) => e.AuthenticationTask = Task.FromResult<ClaimsPrincipal?>(new ClaimsPrincipal());

        await clientSession.ConnectAsync(clientStream);
        var isAuthenticated = await clientSession.AuthenticateAsync(new SshClientCredentials("egs-test", clientKeyPair));
        isAuthenticated.Should().BeTrue();

        var (result, files) = await clientSession.ListDirectoryAsync(testDir, CancellationToken.None);
        
        result.Should().Be(0);
        files.Should().HaveCount(3); // file1.txt, file2.txt, subdir
        
        var file1 = files.FirstOrDefault(f => f.Name == "file1.txt");
        file1.Should().NotBeNull();
        file1!.IsDirectory.Should().BeFalse();
        
        var file2 = files.FirstOrDefault(f => f.Name == "file2.txt");
        file2.Should().NotBeNull();
        file2!.IsDirectory.Should().BeFalse();
        
        var subdirectory = files.FirstOrDefault(f => f.Name == "subdir");
        subdirectory.Should().NotBeNull();
        subdirectory!.IsDirectory.Should().BeTrue();
    }

    [Fact]
    public async Task ListDirectory_ShouldReturnFileNotFoundError_WhenDirectoryDoesNotExist()
    {
        var nonExistentDir = Path.Combine(_path, "nonexistent");

        var serviceId = PortNumberConverter.ToIntegrationId(42428);
        var serverKeyPair = SshAlgorithms.PublicKey.ECDsaSha2Nistp256.GenerateKeyPair();
        var clientKeyPair = SshAlgorithms.PublicKey.ECDsaSha2Nistp256.GenerateKeyPair();

        var config = new SshSessionConfiguration();
        config.Services.Add(typeof(ListDirectoryService), null);

        using var server = new SocketSshServer(config, new TraceSource("Server"));
        server.Credentials = new SshServerCredentials(serverKeyPair);
        using var serverSocket = await SocketFactory.CreateServerSocket(ListenMode.Loopback, serviceId, 1);
        server.SessionAuthenticating += (sender, e) => e.AuthenticationTask = Task.FromResult<ClaimsPrincipal?>(new ClaimsPrincipal());
        server.ExceptionRaised += (_, ex) => throw new Exception("Exception in SSH server", ex);
        _ = server.AcceptSessionsAsync(serverSocket);

        using var clientSocket = await SocketFactory.CreateClientSocket(HyperVAddresses.Loopback, serviceId);
        await using var clientStream = new NetworkStream(clientSocket, true);
        using var clientSession = new SshClientSession(config, new TraceSource("Client"));
        clientSession.Authenticating += (s, e) => e.AuthenticationTask = Task.FromResult<ClaimsPrincipal?>(new ClaimsPrincipal());

        await clientSession.ConnectAsync(clientStream);
        var isAuthenticated = await clientSession.AuthenticateAsync(new SshClientCredentials("egs-test", clientKeyPair));
        isAuthenticated.Should().BeTrue();

        var (result, files) = await clientSession.ListDirectoryAsync(nonExistentDir, CancellationToken.None);
        
        result.Should().Be(ErrorCodes.FileNotFound);
        files.Should().BeEmpty();
    }

    [Fact]
    public async Task DownloadDirectory_ShouldDownloadAllFilesInDirectory()
    {
        // Create test directory structure on source
        var sourceDir = Path.Combine(_path, "sourcedir");
        Directory.CreateDirectory(sourceDir);
        
        // Create files in the directory
        await File.WriteAllTextAsync(Path.Combine(sourceDir, "file1.txt"), "Content 1");
        await File.WriteAllTextAsync(Path.Combine(sourceDir, "file2.txt"), "Content 2");
        
        // Create subdirectory with file
        var subDir = Path.Combine(sourceDir, "subdir");
        Directory.CreateDirectory(subDir);
        await File.WriteAllTextAsync(Path.Combine(subDir, "file3.txt"), "Content 3");

        // Target directory
        var targetDir = Path.Combine(_path, "targetdir");

        var serviceId = PortNumberConverter.ToIntegrationId(42429);
        var serverKeyPair = SshAlgorithms.PublicKey.ECDsaSha2Nistp256.GenerateKeyPair();
        var clientKeyPair = SshAlgorithms.PublicKey.ECDsaSha2Nistp256.GenerateKeyPair();

        var config = new SshSessionConfiguration();
        config.Services.Add(typeof(DownloadFileService), null);
        config.Services.Add(typeof(ListDirectoryService), null);

        using var server = new SocketSshServer(config, new TraceSource("Server"));
        server.Credentials = new SshServerCredentials(serverKeyPair);
        using var serverSocket = await SocketFactory.CreateServerSocket(ListenMode.Loopback, serviceId, 1);
        server.SessionAuthenticating += (sender, e) => e.AuthenticationTask = Task.FromResult<ClaimsPrincipal?>(new ClaimsPrincipal());
        server.ExceptionRaised += (_, ex) => throw new Exception("Exception in SSH server", ex);
        _ = server.AcceptSessionsAsync(serverSocket);

        using var clientSocket = await SocketFactory.CreateClientSocket(HyperVAddresses.Loopback, serviceId);
        await using var clientStream = new NetworkStream(clientSocket, true);
        using var clientSession = new SshClientSession(config, new TraceSource("Client"));
        clientSession.Authenticating += (s, e) => e.AuthenticationTask = Task.FromResult<ClaimsPrincipal?>(new ClaimsPrincipal());

        await clientSession.ConnectAsync(clientStream);
        var isAuthenticated = await clientSession.AuthenticateAsync(new SshClientCredentials("egs-test", clientKeyPair));
        isAuthenticated.Should().BeTrue();

        // First, list the directory to get the files
        var (listResult, files) = await clientSession.ListDirectoryAsync(sourceDir, CancellationToken.None);
        listResult.Should().Be(0);
        files.Should().HaveCount(3); // file1.txt, file2.txt, subdir

        // Create target directory
        Directory.CreateDirectory(targetDir);

        // Download each file
        foreach (var file in files.Where(f => !f.IsDirectory))
        {
            var targetFilePath = Path.Combine(targetDir, file.Name);
            await using var targetStream = new FileStream(targetFilePath, FileMode.Create, FileAccess.Write);
            // Use the source file path directly since FullPath should contain the complete path
            var sourceFilePath = Path.Combine(sourceDir, file.Name);
            var downloadResult = await clientSession.DownloadFileAsync(sourceFilePath, "", targetStream, CancellationToken.None);
            downloadResult.Should().Be(0);
        }

        // Verify downloaded files
        File.Exists(Path.Combine(targetDir, "file1.txt")).Should().BeTrue();
        File.Exists(Path.Combine(targetDir, "file2.txt")).Should().BeTrue();
        
        var content1 = await File.ReadAllTextAsync(Path.Combine(targetDir, "file1.txt"));
        var content2 = await File.ReadAllTextAsync(Path.Combine(targetDir, "file2.txt"));
        
        content1.Should().Be("Content 1");
        content2.Should().Be("Content 2");
    }

    public void Dispose()
    {
        Directory.Delete(_path, true);
    }
}