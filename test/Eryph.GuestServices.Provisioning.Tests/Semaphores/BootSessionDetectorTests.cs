using AwesomeAssertions;
using Eryph.GuestServices.Provisioning.Semaphores;
using Microsoft.Extensions.Logging.Abstractions;

namespace Eryph.GuestServices.Provisioning.Tests.Semaphores;

public sealed class BootSessionDetectorTests : IDisposable
{
    private readonly string _markerPath;
    private readonly string _tempDir;

    public BootSessionDetectorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "egs-boot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _markerPath = Path.Combine(_tempDir, "last-seen-boot.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task IsNewBootAsync_first_call_returns_true_and_persists_marker()
    {
        var clock = new StubBootClock("boot-1");
        var detector = NewDetector(clock);

        var result = await detector.IsNewBootAsync(CancellationToken.None);

        result.Should().BeTrue();
        File.Exists(_markerPath).Should().BeTrue();
    }

    [Fact]
    public async Task IsNewBootAsync_returns_false_when_boot_id_matches_marker()
    {
        var clock = new StubBootClock("boot-1");
        var detector = NewDetector(clock);

        await detector.IsNewBootAsync(CancellationToken.None);
        var second = await detector.IsNewBootAsync(CancellationToken.None);

        second.Should().BeFalse();
    }

    [Fact]
    public async Task IsNewBootAsync_returns_true_when_boot_id_changes()
    {
        var clock = new StubBootClock("boot-1");
        var detector = NewDetector(clock);
        await detector.IsNewBootAsync(CancellationToken.None);

        clock.Current = "boot-2";
        var result = await detector.IsNewBootAsync(CancellationToken.None);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task IsNewBootAsync_TreatsCorruptMarkerAsNewBoot()
    {
        await File.WriteAllTextAsync(_markerPath, "{ not json");
        var detector = NewDetector(new StubBootClock("boot-1"));

        var result = await detector.IsNewBootAsync(CancellationToken.None);

        result.Should().BeTrue();
    }

    // When no marker exists, a clock failure means this is the first run
    // on the machine: treat as new boot so per-boot modules execute.
    [Fact]
    public async Task IsNewBootAsync_ClockFailureWithoutMarker_TreatsAsNewBoot()
    {
        var detector = NewDetector(new ThrowingBootClock());

        var result = await detector.IsNewBootAsync(CancellationToken.None);

        result.Should().BeTrue();
    }

    // When a marker exists, a clock failure means the boot-id source is
    // broken on a system that has previously run. Fail closed: treat as
    // same boot so per-boot modules don't re-run on every cycle.
    [Fact]
    public async Task IsNewBootAsync_ClockFailureWithMarker_FailsClosedAsSameBoot()
    {
        // Stage a marker from a previous run.
        await File.WriteAllTextAsync(
            _markerPath,
            "{\"bootId\":\"prior-boot\",\"detectedAt\":\"2025-01-01T00:00:00+00:00\"}");

        var detector = NewDetector(new ThrowingBootClock());

        var result = await detector.IsNewBootAsync(CancellationToken.None);

        result.Should().BeFalse();
    }

    private BootSessionDetector NewDetector(IBootClock clock) =>
        new(clock, NullLogger<BootSessionDetector>.Instance, _markerPath);

    private sealed class StubBootClock(string initial) : IBootClock
    {
        public string Current { get; set; } = initial;
        public string GetCurrentBootId() => Current;
        public DateTimeOffset GetCurrentBootTime() => DateTimeOffset.UnixEpoch;
    }

    private sealed class ThrowingBootClock : IBootClock
    {
        public string GetCurrentBootId() => throw new InvalidOperationException("CIM unavailable");
        public DateTimeOffset GetCurrentBootTime() => throw new InvalidOperationException("CIM unavailable");
    }
}
