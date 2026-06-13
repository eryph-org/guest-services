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
        var reader = new StubReader(Running("done"));
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
        var reader = new StubReader(Running("error"));
        var kvp = new RecordingDataExchange();

        await Watcher(reader, kvp).WatchAsync(CancellationToken.None);

        kvp.Writes.Should().ContainSingle();
        kvp.Writes[0]["eryph.provisioning.state"].Should().Be("failed");
    }

    [Fact]
    public async Task WatchAsync_reports_running_then_the_final_status()
    {
        var reader = new StubReader(Running("running"), Running("running"), Running("done"));
        var kvp = new RecordingDataExchange();

        await Watcher(reader, kvp).WatchAsync(CancellationToken.None);

        // Deduplicates the repeated "running" — only writes on change.
        kvp.WrittenStates.Should().Equal("running", "completed");
    }

    [Fact]
    public async Task WatchAsync_writes_only_the_provisioning_state_key()
    {
        var reader = new StubReader(Running("running"), Running("done"));
        var kvp = new RecordingDataExchange();

        await Watcher(reader, kvp).WatchAsync(CancellationToken.None);

        kvp.Writes.Should().OnlyContain(w => w.Count == 1 && w.ContainsKey("eryph.provisioning.state"));
    }

    [Fact]
    public async Task WatchAsync_does_nothing_when_cloud_init_is_not_installed()
    {
        var reader = new StubReader(CloudInitProbe.NotInstalled);
        var kvp = new RecordingDataExchange();

        await Watcher(reader, kvp).WatchAsync(CancellationToken.None);

        kvp.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task WatchAsync_keeps_polling_while_cloud_init_has_no_status_yet()
    {
        // Installed but no parseable status yet (cloud-init still starting) must
        // NOT end the watcher — it should keep polling until a real status.
        var reader = new StubReader(Running(null), Running(null), Running("done"));
        var kvp = new RecordingDataExchange();

        await Watcher(reader, kvp).WatchAsync(CancellationToken.None);

        kvp.WrittenStates.Should().Equal("completed");
    }

    [Fact]
    public async Task WatchAsync_does_not_write_for_not_run()
    {
        // "not run" then "done": nothing for "not run", then the terminal write.
        var reader = new StubReader(Running("not run"), Running("done"));
        var kvp = new RecordingDataExchange();

        await Watcher(reader, kvp).WatchAsync(CancellationToken.None);

        kvp.WrittenStates.Should().Equal("completed");
    }

    [Fact]
    public async Task WatchAsync_retries_the_same_state_after_a_failed_write()
    {
        // The first write throws; lastWritten must NOT advance, so the second
        // identical "running" poll re-attempts the write instead of skipping it.
        var reader = new StubReader(Running("running"), Running("running"));
        var kvp = new FlakyDataExchange(failFirst: true);

        await Watcher(reader, kvp).WatchAsync(CancellationToken.None);

        kvp.Attempts.Should().Equal("running", "running");
    }

    private static CloudInitProbe Running(string? status) => CloudInitProbe.Running(status);

    private static CloudInitStatusWatcher Watcher(ICloudInitStatusReader reader, IGuestDataExchange kvp) =>
        new(reader, kvp, NullLogger<CloudInitStatusWatcher>.Instance, TimeSpan.Zero);

    private sealed class StubReader(params CloudInitProbe[] probes) : ICloudInitStatusReader
    {
        private readonly Queue<CloudInitProbe> _probes = new(probes);

        // Once the script is exhausted, report not-installed so a non-terminal
        // script still ends the loop deterministically (termination of a script
        // that ends in a terminal status is driven by IsTerminal, before this).
        public Task<CloudInitProbe> ReadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(_probes.Count > 0 ? _probes.Dequeue() : CloudInitProbe.NotInstalled);
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

    // Records every write attempt; the first one throws when failFirst is set.
    private sealed class FlakyDataExchange(bool failFirst) : IGuestDataExchange
    {
        private int _calls;
        public List<string?> Attempts { get; } = [];

        public Task<IReadOnlyDictionary<string, string>> GetExternalDataAsync()
            => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());

        public Task<IReadOnlyDictionary<string, string>> GetGuestDataAsync()
            => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());

        public Task SetGuestValuesAsync(IReadOnlyDictionary<string, string?> values)
        {
            _calls++;
            Attempts.Add(values.TryGetValue("eryph.provisioning.state", out var v) ? v : null);
            if (failFirst && _calls == 1)
                throw new InvalidOperationException("kvp transiently unavailable");
            return Task.CompletedTask;
        }
    }
}
