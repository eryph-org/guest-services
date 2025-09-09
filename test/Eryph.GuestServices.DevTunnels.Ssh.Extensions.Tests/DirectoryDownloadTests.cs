using AwesomeAssertions;
using Eryph.GuestServices.DevTunnels.Ssh.Extensions.Services;

namespace Eryph.GuestServices.DevTunnels.Ssh.Extensions.Tests;

[Collection("e2e")]
public sealed class DirectoryDownloadTests : IDisposable
{
    private readonly string _path;

    public DirectoryDownloadTests()
    {
        _path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_path);
    }

    [Fact]
    public async Task DownloadDirectoryLogic_NonRecursive_ShouldDownloadOnlyRootFiles()
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

        using var helper = new SshTestHelper();
        await helper.SetupAsync(typeof(DownloadFileService), typeof(ListDirectoryService));

        var result = await helper.ClientSession.DownloadDirectoryAsync(
            sourceDir,
            targetDir,
            overwrite: false,
            recursive: false);
        
        result.Should().Be(0);

        // Verify only root files were downloaded
        File.Exists(Path.Combine(targetDir, "file1.txt")).Should().BeTrue();
        File.Exists(Path.Combine(targetDir, "file2.txt")).Should().BeTrue();
        
        var content1 = await File.ReadAllTextAsync(Path.Combine(targetDir, "file1.txt"));
        var content2 = await File.ReadAllTextAsync(Path.Combine(targetDir, "file2.txt"));
        
        content1.Should().Be("Content 1");
        content2.Should().Be("Content 2");
        
        // Verify subdirectory was NOT downloaded (non-recursive behavior)
        Directory.Exists(Path.Combine(targetDir, "subdir")).Should().BeFalse();
        File.Exists(Path.Combine(targetDir, "subdir", "file3.txt")).Should().BeFalse();
    }

    [Fact]
    public async Task DownloadDirectoryLogic_Recursive_ShouldDownloadAllFilesIncludingSubdirectories()
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

        using var helper = new SshTestHelper();
        await helper.SetupAsync(typeof(DownloadFileService), typeof(ListDirectoryService));

        var result = await helper.ClientSession.DownloadDirectoryAsync(
            sourceDir,
            targetDir,
            overwrite: false,
            recursive: true);
        
        result.Should().Be(0);

        // Verify root files were downloaded
        File.Exists(Path.Combine(targetDir, "root1.txt")).Should().BeTrue();
        File.Exists(Path.Combine(targetDir, "root2.txt")).Should().BeTrue();
        
        var rootContent1 = await File.ReadAllTextAsync(Path.Combine(targetDir, "root1.txt"));
        var rootContent2 = await File.ReadAllTextAsync(Path.Combine(targetDir, "root2.txt"));
        
        rootContent1.Should().Be("Root Content 1");
        rootContent2.Should().Be("Root Content 2");
        
        // Verify level1 subdirectory and its files were downloaded
        File.Exists(Path.Combine(targetDir, "level1", "level1_file1.txt")).Should().BeTrue();
        File.Exists(Path.Combine(targetDir, "level1", "level1_file2.txt")).Should().BeTrue();
        
        var level1Content1 = await File.ReadAllTextAsync(Path.Combine(targetDir, "level1", "level1_file1.txt"));
        var level1Content2 = await File.ReadAllTextAsync(Path.Combine(targetDir, "level1", "level1_file2.txt"));
        
        level1Content1.Should().Be("Level1 Content 1");
        level1Content2.Should().Be("Level1 Content 2");
        
        // Verify level2 nested subdirectory and its files were downloaded
        File.Exists(Path.Combine(targetDir, "level1", "level2", "level2_file.txt")).Should().BeTrue();
        
        var level2Content = await File.ReadAllTextAsync(Path.Combine(targetDir, "level1", "level2", "level2_file.txt"));
        level2Content.Should().Be("Level2 Content");
    }

    public void Dispose()
    {
        Directory.Delete(_path, true);
    }
}