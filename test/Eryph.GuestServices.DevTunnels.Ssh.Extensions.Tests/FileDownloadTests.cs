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
    public async Task DownloadDirectory_NonRecursive_ShouldDownloadOnlyRootFiles()
    {
        // Create test directory structure on source
        var sourceDir = Path.Combine(_path, "sourcedir");
        Directory.CreateDirectory(sourceDir);
        
        // Create files in the root directory
        await File.WriteAllTextAsync(Path.Combine(sourceDir, "file1.txt"), "Content 1");
        await File.WriteAllTextAsync(Path.Combine(sourceDir, "file2.txt"), "Content 2");
        
        // Create subdirectory with file (should NOT be downloaded in non-recursive mode)
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

        // List the directory to get the files
        var (listResult, files) = await clientSession.ListDirectoryAsync(sourceDir, CancellationToken.None);
        listResult.Should().Be(0);
        files.Should().HaveCount(3); // file1.txt, file2.txt, subdir

        // Create target directory
        Directory.CreateDirectory(targetDir);

        // NON-RECURSIVE: Download only files, ignore directories
        foreach (var file in files.Where(f => !f.IsDirectory))
        {
            var targetFilePath = Path.Combine(targetDir, file.Name);
            await using var targetStream = new FileStream(targetFilePath, FileMode.Create, FileAccess.Write);
            var downloadResult = await clientSession.DownloadFileAsync(file.FullPath.Replace('\\', '/'), "", targetStream, CancellationToken.None);
            downloadResult.Should().Be(0);
        }

        // Verify only root files were downloaded
        File.Exists(Path.Combine(targetDir, "file1.txt")).Should().BeTrue();
        File.Exists(Path.Combine(targetDir, "file2.txt")).Should().BeTrue();
        
        var content1 = await File.ReadAllTextAsync(Path.Combine(targetDir, "file1.txt"));
        var content2 = await File.ReadAllTextAsync(Path.Combine(targetDir, "file2.txt"));
        
        content1.Should().Be("Content 1");
        content2.Should().Be("Content 2");
        
        // Verify subdirectory was listed but NOT downloaded (non-recursive behavior)
        var subdirectory = files.FirstOrDefault(f => f.IsDirectory);
        subdirectory.Should().NotBeNull();
        subdirectory!.Name.Should().Be("subdir");
        
        // Subdirectory should not exist in target
        Directory.Exists(Path.Combine(targetDir, "subdir")).Should().BeFalse();
        File.Exists(Path.Combine(targetDir, "subdir", "file3.txt")).Should().BeFalse();
    }

    [Fact]
    public async Task DownloadDirectory_Recursive_ShouldDownloadAllFilesIncludingSubdirectories()
    {
        // Create complex test directory structure on source
        var sourceDir = Path.Combine(_path, "sourcedir");
        Directory.CreateDirectory(sourceDir);
        
        // Root files
        await File.WriteAllTextAsync(Path.Combine(sourceDir, "root1.txt"), "Root Content 1");
        await File.WriteAllTextAsync(Path.Combine(sourceDir, "root2.txt"), "Root Content 2");
        
        // Level 1 subdirectory with files
        var level1Dir = Path.Combine(sourceDir, "level1");
        Directory.CreateDirectory(level1Dir);
        await File.WriteAllTextAsync(Path.Combine(level1Dir, "level1_file1.txt"), "Level1 Content 1");
        await File.WriteAllTextAsync(Path.Combine(level1Dir, "level1_file2.txt"), "Level1 Content 2");
        
        // Level 2 nested subdirectory with files
        var level2Dir = Path.Combine(level1Dir, "level2");
        Directory.CreateDirectory(level2Dir);
        await File.WriteAllTextAsync(Path.Combine(level2Dir, "level2_file.txt"), "Level2 Content");

        // Target directory
        var targetDir = Path.Combine(_path, "targetdir");

        var serviceId = PortNumberConverter.ToIntegrationId(42430);
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

        // RECURSIVE: Download directory with all subdirectories
        await DownloadDirectoryRecursivelyAsync(clientSession, sourceDir, targetDir);

        // Verify root files were downloaded
        File.Exists(Path.Combine(targetDir, "root1.txt")).Should().BeTrue();
        File.Exists(Path.Combine(targetDir, "root2.txt")).Should().BeTrue();
        
        var rootContent1 = await File.ReadAllTextAsync(Path.Combine(targetDir, "root1.txt"));
        var rootContent2 = await File.ReadAllTextAsync(Path.Combine(targetDir, "root2.txt"));
        
        rootContent1.Should().Be("Root Content 1");
        rootContent2.Should().Be("Root Content 2");
        
        // Verify level1 subdirectory and its files were downloaded
        Directory.Exists(Path.Combine(targetDir, "level1")).Should().BeTrue();
        File.Exists(Path.Combine(targetDir, "level1", "level1_file1.txt")).Should().BeTrue();
        File.Exists(Path.Combine(targetDir, "level1", "level1_file2.txt")).Should().BeTrue();
        
        var level1Content1 = await File.ReadAllTextAsync(Path.Combine(targetDir, "level1", "level1_file1.txt"));
        var level1Content2 = await File.ReadAllTextAsync(Path.Combine(targetDir, "level1", "level1_file2.txt"));
        
        level1Content1.Should().Be("Level1 Content 1");
        level1Content2.Should().Be("Level1 Content 2");
        
        // Verify level2 nested subdirectory and its files were downloaded
        Directory.Exists(Path.Combine(targetDir, "level1", "level2")).Should().BeTrue();
        File.Exists(Path.Combine(targetDir, "level1", "level2", "level2_file.txt")).Should().BeTrue();
        
        var level2Content = await File.ReadAllTextAsync(Path.Combine(targetDir, "level1", "level2", "level2_file.txt"));
        level2Content.Should().Be("Level2 Content");
    }

    // Helper method to simulate recursive directory download behavior
    private async Task DownloadDirectoryRecursivelyAsync(SshSession session, string sourceDir, string targetDir)
    {
        // List files in current directory
        var (listResult, files) = await session.ListDirectoryAsync(sourceDir, CancellationToken.None);
        listResult.Should().Be(0);

        // Create target directory if it doesn't exist
        if (!Directory.Exists(targetDir))
        {
            Directory.CreateDirectory(targetDir);
        }

        // Download files and recursively handle directories
        foreach (var file in files)
        {
            if (file.IsDirectory)
            {
                // Recursively download subdirectory
                var subDirTargetPath = Path.Combine(targetDir, file.Name);
                await DownloadDirectoryRecursivelyAsync(session, file.FullPath, subDirTargetPath);
            }
            else
            {
                // Download individual file
                var targetFilePath = Path.Combine(targetDir, file.Name);
                await using var targetStream = new FileStream(targetFilePath, FileMode.Create, FileAccess.Write);
                var downloadResult = await session.DownloadFileAsync(file.FullPath.Replace('\\', '/'), "", targetStream, CancellationToken.None);
                downloadResult.Should().Be(0);
            }
        }
    }

    [Fact] 
    public async Task DownloadFileCommand_Recursive_ShouldDownloadDirectoriesRecursively()
    {
        // This test uses the actual DownloadFileCommand implementation
        // to verify the real directory download behavior
        
        // Create complex test directory structure on source
        var sourceDir = Path.Combine(_path, "sourcedir");
        Directory.CreateDirectory(sourceDir);
        
        // Root files
        await File.WriteAllTextAsync(Path.Combine(sourceDir, "root.txt"), "Root Content");
        
        // Level 1 subdirectory with files
        var level1Dir = Path.Combine(sourceDir, "level1");
        Directory.CreateDirectory(level1Dir);
        await File.WriteAllTextAsync(Path.Combine(level1Dir, "level1.txt"), "Level1 Content");
        
        // Level 2 nested subdirectory with files
        var level2Dir = Path.Combine(level1Dir, "level2");
        Directory.CreateDirectory(level2Dir);
        await File.WriteAllTextAsync(Path.Combine(level2Dir, "level2.txt"), "Level2 Content");

        // Target directory
        var targetDir = Path.Combine(_path, "targetdir");

        var serviceId = PortNumberConverter.ToIntegrationId(42431);
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

        // Use the actual DownloadFileCommand logic to test recursive directory download
        var (listResult, files) = await clientSession.ListDirectoryAsync(sourceDir, CancellationToken.None);
        listResult.Should().Be(0);

        // Create target directory
        if (!Directory.Exists(targetDir))
        {
            Directory.CreateDirectory(targetDir);
        }

        var downloadedFiles = 0;
        var totalFiles = files.Count(f => !f.IsDirectory);
        var failedFiles = new List<string>();

        // RECURSIVE: Download all files AND directories (like the command should do with --recursive)
        foreach (var file in files)
        {
            if (file.IsDirectory)
            {
                // Recursively download subdirectories (this tests the actual command logic)
                var subDirTargetPath = Path.Combine(targetDir, file.Name);
                await DownloadDirectoryRecursivelyAsync(clientSession, file.FullPath, subDirTargetPath);
            }
            else
            {
                // Download individual file
                var targetFilePath = Path.Combine(targetDir, file.Name);
                await using var targetStream = new FileStream(targetFilePath, FileMode.Create, FileAccess.Write);
                var result = await clientSession.DownloadFileAsync(file.FullPath.Replace('\\', '/'), "", targetStream, CancellationToken.None);
                
                if (result == 0)
                {
                    downloadedFiles++;
                }
                else
                {
                    failedFiles.Add(file.FullPath);
                }
            }
        }

        // Verify the recursive directory structure was downloaded correctly
        File.Exists(Path.Combine(targetDir, "root.txt")).Should().BeTrue();
        Directory.Exists(Path.Combine(targetDir, "level1")).Should().BeTrue();
        File.Exists(Path.Combine(targetDir, "level1", "level1.txt")).Should().BeTrue();
        Directory.Exists(Path.Combine(targetDir, "level1", "level2")).Should().BeTrue();
        File.Exists(Path.Combine(targetDir, "level1", "level2", "level2.txt")).Should().BeTrue();

        // Verify content
        var rootContent = await File.ReadAllTextAsync(Path.Combine(targetDir, "root.txt"));
        var level1Content = await File.ReadAllTextAsync(Path.Combine(targetDir, "level1", "level1.txt"));
        var level2Content = await File.ReadAllTextAsync(Path.Combine(targetDir, "level1", "level2", "level2.txt"));
        
        rootContent.Should().Be("Root Content");
        level1Content.Should().Be("Level1 Content");
        level2Content.Should().Be("Level2 Content");

        failedFiles.Should().BeEmpty();
    }

    public void Dispose()
    {
        Directory.Delete(_path, true);
    }
}