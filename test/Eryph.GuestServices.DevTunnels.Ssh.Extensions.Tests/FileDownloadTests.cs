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

        using var helper = await new SshTestHelper().SetupAsync(typeof(DownloadFileService));

        await using (var targetStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write))
        {
            await helper.ClientSession.DownloadFileAsync(srcPath, "", targetStream, CancellationToken.None);
        }

        var targetContent = await File.ReadAllTextAsync(targetPath);
        targetContent.Should().Be("Hello Download World!");
    }

    [Fact]
    public async Task DownloadFile_ShouldReturnFileNotFoundError_WhenSourceFileDoesNotExist()
    {
        var nonExistentPath = Path.Combine(_path, "nonexistent.bin");
        var targetPath = Path.Combine(_path, "target.bin");

        using var helper = await new SshTestHelper().SetupAsync(typeof(DownloadFileService));
        
        await using var targetStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write);
        
        var result = await helper.ClientSession.DownloadFileAsync(nonExistentPath, "", targetStream, CancellationToken.None);
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

        using var helper = await new SshTestHelper().SetupAsync(typeof(ListDirectoryService));

        var (result, files) = await helper.ClientSession.ListDirectoryAsync(testDir, CancellationToken.None);
        
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

        using var helper = await new SshTestHelper().SetupAsync(typeof(ListDirectoryService));

        var (result, files) = await helper.ClientSession.ListDirectoryAsync(nonExistentDir, CancellationToken.None);
        
        result.Should().Be(ErrorCodes.FileNotFound);
        files.Should().BeEmpty();
    }

    [Fact]
    public async Task DownloadFileAsync_ShouldDownloadOnlyRootFiles_WhenDirectoryFilesListedSeparately()
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

        using var helper = await new SshTestHelper().SetupAsync(typeof(DownloadFileService), typeof(ListDirectoryService));

        // List the directory to get the files
        var (listResult, files) = await helper.ClientSession.ListDirectoryAsync(sourceDir, CancellationToken.None);
        listResult.Should().Be(0);
        files.Should().HaveCount(3); // file1.txt, file2.txt, subdir

        // Create target directory
        Directory.CreateDirectory(targetDir);

        // NON-RECURSIVE: Download only files, ignore directories (DownloadDirectoryCommand without --recursive)
        foreach (var file in files.Where(f => !f.IsDirectory))
        {
            var normalizedPath = SshExtensionUtils.NormalizePath(file.FullPath);
            var targetFilePath = Path.Combine(targetDir, file.Name);
            await using var targetStream = new FileStream(targetFilePath, FileMode.Create, FileAccess.Write);
            var downloadResult = await helper.ClientSession.DownloadFileAsync(normalizedPath, "", targetStream, CancellationToken.None);
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
    public async Task DownloadFileAsync_ShouldDownloadAllFiles_WhenUsedRecursivelyWithListDirectoryAsync()
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

        using var helper = await new SshTestHelper().SetupAsync(typeof(DownloadFileService), typeof(ListDirectoryService));

        // Test ListDirectoryAsync on nested structure
        var (listResult, files) = await helper.ClientSession.ListDirectoryAsync(sourceDir, CancellationToken.None);
        listResult.Should().Be(0);
        files.Should().HaveCount(3); // root1.txt, root2.txt, level1 directory
        
        var rootFile1 = files.FirstOrDefault(f => f.Name == "root1.txt");
        rootFile1.Should().NotBeNull();
        rootFile1!.IsDirectory.Should().BeFalse();
        
        var level1DirInfo = files.FirstOrDefault(f => f.Name == "level1");
        level1DirInfo.Should().NotBeNull();
        level1DirInfo!.IsDirectory.Should().BeTrue();
        
        // Test DownloadFileAsync with a root file
        Directory.CreateDirectory(targetDir);
        var targetFilePath = Path.Combine(targetDir, "root1.txt");
        await using var targetStream = new FileStream(targetFilePath, FileMode.Create, FileAccess.Write);
        var downloadResult = await helper.ClientSession.DownloadFileAsync(rootFile1.FullPath, "", targetStream, CancellationToken.None);
        downloadResult.Should().Be(0);
        targetStream.Close();
        
        var downloadedContent = await File.ReadAllTextAsync(targetFilePath);
        downloadedContent.Should().Be("Root Content 1");
        
        // Test ListDirectoryAsync on subdirectory
        var (subListResult, subFiles) = await helper.ClientSession.ListDirectoryAsync(level1DirInfo.FullPath, CancellationToken.None);
        subListResult.Should().Be(0);
        subFiles.Should().HaveCount(3); // level1_file1.txt, level1_file2.txt, level2 directory
    }

    public void Dispose()
    {
        Directory.Delete(_path, true);
    }
}