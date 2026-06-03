using AwesomeAssertions;
using Eryph.GuestServices.HvDataExchange.Guest;
using Eryph.GuestServices.Service.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Eryph.GuestServices.Service.Tests;

public class CloudInitStatusWatcherTests
{
    [Fact]
    public async Task WatchAsync_writes_completed_and_stops_when_cloud_init_is_done()
    {
        var reader = new StubReader("done");
        var kvp = new RecordingDataExchange();

        await Watcher(reader, kvp).WatchAsync(CancellationToken.None);

        kvp.Writes.Should().ContainSingle();
        kvp.Writes[0].Should().ContainSingle()
            .Which.Should().BeEquivalentTo(
                new KeyValuePair<string, string?>("eryph.provisioning.state", "completed"));
    }

    [Fact]
    public async Task WatchAsync_writes_failed_when_cloud_init_errors()
    {
        var reader = new StubReader("error");
        var kvp = new RecordingDataExchange();

        await Watcher(reader, kvp).WatchAsync(CancellationToken.None);

        kvp.Writes.Should().ContainSingle();
        kvp.Writes[0]["eryph.provisioning.state"].Should().Be("failed");
    }

    [Fact]
    public async Task WatchAsync_reports_running_then_the_final_status()
    {
        var reader = new StubReader("running", "running", "done");
        var kvp = new RecordingDataExchange();

        await Watcher(reader, kvp).WatchAsync(CancellationToken.None);

        // Deduplicates the repeated "running" — only writes on change.
        kvp.WrittenStates.Should().Equal("running", "completed");
    }

    [Fact]
    public async Task WatchAsync_writes_only_the_provisioning_state_key()
    {
        var reader = new StubReader("running", "done");
        var kvp = new RecordingDataExchange();

        await Watcher(reader, kvp).WatchAsync(CancellationToken.None);

        kvp.Writes.Should().OnlyContain(w => w.Count == 1 && w.ContainsKey("eryph.provisioning.state"));
    }

    [Fact]
    public async Task WatchAsync_does_nothing_when_cloud_init_is_unavailable()
    {
        var reader = new StubReader((string?)null);
        var kvp = new RecordingDataExchange();

        await Watcher(reader, kvp).WatchAsync(CancellationToken.None);

        kvp.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task WatchAsync_does_not_write_for_not_run()
    {
        // "not run" then "done": nothing for "not run", then the terminal write.
        var reader = new StubReader("not run", "done");
        var kvp = new RecordingDataExchange();

        await Watcher(reader, kvp).WatchAsync(CancellationToken.None);

        kvp.WrittenStates.Should().Equal("completed");
    }

    private static CloudInitStatusWatcher Watcher(ICloudInitStatusReader reader, IGuestDataExchange kvp) =>
        new(reader, kvp, NullLogger<CloudInitStatusWatcher>.Instance, TimeSpan.Zero);

    private sealed class StubReader(params string?[] statuses) : ICloudInitStatusReader
    {
        private readonly Queue<string?> _statuses = new(statuses);

        public Task<string?> GetStatusAsync(CancellationToken cancellationToken) =>
            // Once the script is exhausted, return null (unavailable) so a
            // non-terminal script still ends the loop deterministically.
            Task.FromResult(_statuses.Count > 0 ? _statuses.Dequeue() : (string?)null);
    }

    private sealed class RecordingDataExchange : IGuestDataExchange
    {
        public List<IReadOnlyDictionary<string, string?>> Writes { get; } = [];

        public IEnumerable<string?> WrittenStates =>
            Writes.Select(w => w.TryGetValue("eryph.provisioning.state", out var v) ? v : null);

        public Task<IReadOnlyDictionary<string, string>> GetExternalDataAsync()
            => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());

        public Task<IReadOnlyDictionary<string, string>> GetGuestDataAsync()
            => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());

        public Task SetGuestValuesAsync(IReadOnlyDictionary<string, string?> values)
        {
            Writes.Add(new Dictionary<string, string?>(values, StringComparer.Ordinal));
            return Task.CompletedTask;
        }
    }
}
