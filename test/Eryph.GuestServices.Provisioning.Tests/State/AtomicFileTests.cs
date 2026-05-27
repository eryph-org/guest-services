using AwesomeAssertions;
using Eryph.GuestServices.Provisioning.State;
using Microsoft.Extensions.Logging.Abstractions;

namespace Eryph.GuestServices.Provisioning.Tests.State;

/// <summary>
/// Regression for the WS2025 provisioning crash: replacing state.json threw
/// UnauthorizedAccessException/IOException (antivirus / a concurrent reader
/// briefly holding the destination). The replace must retry and recover.
/// </summary>
public sealed class AtomicFileTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "egs-atomic-" + Guid.NewGuid().ToString("N"));

    public AtomicFileTests() => Directory.CreateDirectory(_dir);

    [Fact]
    public async Task ReplaceWithRetry_recovers_after_a_transient_failure()
    {
        // Force File.Move to fail transiently in a cross-platform, deterministic
        // way: the destination's parent directory is missing at first (File.Move
        // throws DirectoryNotFoundException, an IOException, on every OS), then a
        // background task creates it so a later retry succeeds. This exercises the
        // catch/backoff/recover loop without relying on Windows-specific file
        // locking semantics.
        var destDir = Path.Combine(_dir, "subdir");
        var dest = Path.Combine(destDir, "state.json");
        var temp = Path.Combine(_dir, "state.json.tmp");
        File.WriteAllText(temp, "new");

        var createDir = Task.Run(async () =>
        {
            await Task.Delay(120);
            Directory.CreateDirectory(destDir);
        });

        await AtomicFile.ReplaceWithRetryAsync(temp, dest, NullLogger.Instance, CancellationToken.None);
        await createDir;

        File.ReadAllText(dest).Should().Be("new");
        File.Exists(temp).Should().BeFalse();
    }

    [Fact]
    public async Task ReplaceWithRetry_throws_after_exhausting_attempts()
    {
        // A destination directory that never appears must surface the failure
        // (rather than retrying forever) once the attempt budget is exhausted.
        var dest = Path.Combine(_dir, "never", "state.json");
        var temp = Path.Combine(_dir, "state.json.tmp");
        File.WriteAllText(temp, "new");

        var act = async () => await AtomicFile.ReplaceWithRetryAsync(
            temp, dest, NullLogger.Instance, CancellationToken.None, maxAttempts: 3);

        await act.Should().ThrowAsync<IOException>();
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }
}
