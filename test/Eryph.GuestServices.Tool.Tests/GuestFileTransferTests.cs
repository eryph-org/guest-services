using System.Diagnostics;
using AwesomeAssertions;
using Eryph.GuestServices.DevTunnels.Ssh.Extensions;
using Eryph.GuestServices.Tool;
using Microsoft.DevTunnels.Ssh;

namespace Eryph.GuestServices.Tool.Tests;

public class GuestFileTransferTests : IDisposable
{
    private readonly string _dir;

    public GuestFileTransferTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "egs-transfer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // best effort cleanup
        }
    }

    [Fact]
    public async Task DownloadFileAsync_TargetExistsWithoutOverwrite_PreservesExistingFile()
    {
        // Regression: refusing to overwrite must leave the existing file intact.
        // The previous per-command flow deleted the target on any non-zero result,
        // so a FileExists refusal would destroy the very file it declined to
        // replace. The refusal returns before the session is used, so a bare
        // (unconnected) session is enough to exercise it.
        var target = Path.Combine(_dir, "existing.txt");
        await File.WriteAllTextAsync(target, "original content");
        using var session = new SshClientSession(new SshSessionConfiguration(), new TraceSource("test"));

        var result = await GuestFileTransfer.DownloadFileAsync(
            session, "/remote/source.txt", target, overwrite: false);

        result.Should().Be(ErrorCodes.FileExists);
        File.Exists(target).Should().BeTrue();
        (await File.ReadAllTextAsync(target)).Should().Be("original content");
    }

    [Fact]
    public async Task UploadFileAsync_MissingSource_FailsWithoutUsingSession()
    {
        // The missing-source check happens before any transport use, so it is the
        // same fast failure on both the Hyper-V and eryph transports.
        var missing = Path.Combine(_dir, "does-not-exist.bin");
        using var session = new SshClientSession(new SshSessionConfiguration(), new TraceSource("test"));

        var result = await GuestFileTransfer.UploadFileAsync(
            session, missing, "/remote/target.bin", overwrite: false);

        result.Should().Be(-1);
    }

    [Fact]
    public async Task UploadDirectoryAsync_MissingSource_FailsWithoutUsingSession()
    {
        // The directory upload's missing-source guard now lives only in the shared
        // helper, so pin it: a non-existent source fails before any transport use.
        var missing = Path.Combine(_dir, "no-such-dir");
        using var session = new SshClientSession(new SshSessionConfiguration(), new TraceSource("test"));

        var result = await GuestFileTransfer.UploadDirectoryAsync(
            session, missing, "/remote/target", overwrite: false, recursive: false);

        result.Should().Be(-1);
    }
}
