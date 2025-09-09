using AwesomeAssertions;
using Eryph.GuestServices.DevTunnels.Ssh.Extensions.Services;

namespace Eryph.GuestServices.DevTunnels.Ssh.Extensions.Tests;

[Collection("e2e")]
public sealed class FileUploadTests : IDisposable
{
    private readonly string _path;

    public FileUploadTests()
    {
        _path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_path);
    }

    [Fact]
    public async Task UploadFile_ShouldTransferFileFromClientToServer()
    {
        var srcPath = Path.Combine(_path, "src.bin");
        await File.WriteAllTextAsync(srcPath, "Hello World!");
        var targetPath = Path.Combine(_path, "target.bin");

        using var helper = new SshTestHelper();
        await helper.SetupAsync(typeof(UploadFileService));

        await using (var srcStream = new FileStream(srcPath, FileMode.Open, FileAccess.Read))
        {
            await helper.ClientSession.TransferFileAsync(targetPath, "", srcStream, false, CancellationToken.None);
        }

        var targetContent = await File.ReadAllTextAsync(targetPath);
        targetContent.Should().Be("Hello World!");
    }


    public void Dispose()
    {
        Directory.Delete(_path, true);
    }
}
