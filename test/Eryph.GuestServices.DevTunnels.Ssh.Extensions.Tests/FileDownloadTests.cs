using AwesomeAssertions;
using Eryph.GuestServices.DevTunnels.Ssh.Extensions.Services;

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

        using var helper = new SshTestHelper();
        await helper.SetupAsync(typeof(DownloadFileService));

        await using (var targetStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write))
        {
            await helper.ClientSession.DownloadFileAsync(srcPath, targetStream, CancellationToken.None);
        }

        var targetContent = await File.ReadAllTextAsync(targetPath);
        targetContent.Should().Be("Hello Download World!");
    }

    [Fact]
    public async Task DownloadFile_ShouldReturnFileNotFoundError_WhenSourceFileDoesNotExist()
    {
        var nonExistentPath = Path.Combine(_path, "nonexistent.bin");
        var targetPath = Path.Combine(_path, "target.bin");

        using var helper = new SshTestHelper();
        await helper.SetupAsync(typeof(DownloadFileService));
        
        await using var targetStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write);
        
        var result = await helper.ClientSession.DownloadFileAsync(nonExistentPath, targetStream, CancellationToken.None);
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

        using var helper = new SshTestHelper();
        await helper.SetupAsync(typeof(ListDirectoryService));

        var (result, files) = await helper.ClientSession.ListDirectoryAsync(testDir, CancellationToken.None);
        
        result.Should().Be(0);
        files.Should().HaveCount(3); // file1.txt, file2.txt, subdirectory
        
        var file1 = files.FirstOrDefault(f => f.Name == "file1.txt");
        file1.Should().NotBeNull();
        file1.IsDirectory.Should().BeFalse();
        
        var file2 = files.FirstOrDefault(f => f.Name == "file2.txt");
        file2.Should().NotBeNull();
        file2.IsDirectory.Should().BeFalse();
        
        var subdirectory = files.FirstOrDefault(f => f.Name == "subdir");
        subdirectory.Should().NotBeNull();
        subdirectory.IsDirectory.Should().BeTrue();
    }

    [Fact]
    public async Task ListDirectory_ShouldReturnFileNotFoundError_WhenDirectoryDoesNotExist()
    {
        var nonExistentDir = Path.Combine(_path, "nonexistent");

        using var helper = new SshTestHelper();
        await helper.SetupAsync(typeof(ListDirectoryService));

        var (result, files) = await helper.ClientSession.ListDirectoryAsync(nonExistentDir, CancellationToken.None);
        
        result.Should().Be(ErrorCodes.FileNotFound);
        files.Should().BeEmpty();
    }


    public void Dispose()
    {
        Directory.Delete(_path, true);
    }
}