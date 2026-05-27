using AwesomeAssertions;
using Eryph.GuestServices.Provisioning.State;
using Microsoft.Extensions.Logging.Abstractions;

namespace Eryph.GuestServices.Provisioning.Tests.State;

/// <summary>
/// Regression for the WS2025 provisioning crash: replacing state.json threw
/// UnauthorizedAccessException/IOException when antivirus or a concurrent reader
/// briefly held the destination. The replace must retry and recover, and our
/// own readers must not block it.
/// </summary>
public sealed class AtomicFileTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "egs-atomic-" + Guid.NewGuid().ToString("N"));

    public AtomicFileTests() => Directory.CreateDirectory(_dir);

    [Fact]
    public async Task ReplaceWithRetry_recovers_when_destination_is_briefly_locked()
    {
        var dest = Path.Combine(_dir, "state.json");
        File.WriteAllText(dest, "old");

        // Hold the destination with NO sharing (blocks the replace), then release
        // it shortly after — the retry loop must recover and complete the replace.
        var blocker = File.Open(dest, FileMode.Open, FileAccess.Read, FileShare.None);
        var release = Task.Run(async () =>
        {
            await Task.Delay(120);
            blocker.Dispose();
        });

        var temp = Path.Combine(_dir, "state.json.tmp");
        File.WriteAllText(temp, "new");

        await AtomicFile.ReplaceWithRetryAsync(temp, dest, NullLogger.Instance, CancellationToken.None);
        await release;

        File.ReadAllText(dest).Should().Be("new");
        File.Exists(temp).Should().BeFalse();
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }
}
