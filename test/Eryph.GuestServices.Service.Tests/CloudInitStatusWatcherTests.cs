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

        // The state key is written to "failed" (a failure-detail event is also
        // emitted — covered separately).
        kvp.WrittenStates.Should().Contain("failed");
    }

    [Fact]
    public async Task WatchAsync_emits_failure_detail_event_with_cloud_init_error_summary()
    {
        // On a failed run the watcher surfaces cloud-init's own error summary as a
        // single CLOUD_INIT FAIL event, so the host log names WHAT failed without
        // an operator entering the guest.
        var reader = new StubReader(CloudInitProbe.Running(
            "error",
            errors: ["Runparts: 1 failures (001-bootstrap.sh) in 3 attempted commands"],
            recoverableErrors: ["WARNING: deprecated key 'foo'"]));
        var kvp = new RecordingDataExchange();

        await Watcher(reader, kvp).WatchAsync(CancellationToken.None);

        var failChunks = kvp.Writes
            .SelectMany(w => w)
            .Where(kv => kv.Key.StartsWith("CLOUD_INIT|", StringComparison.Ordinal) && kv.Value != null)
            .ToList();
        // Fits in a single KVP value (one entry), so the host cannot drop part of it.
        failChunks.Should().ContainSingle();
        failChunks[0].Value.Should().Contain("\"result\":\"FAIL\"");
        failChunks[0].Value.Should().Contain("001-bootstrap.sh");
        failChunks[0].Value.Should().Contain("WARNING: deprecated key");
    }

    [Fact]
    public async Task WatchAsync_reuses_cloud_inits_current_boot_incarnation_for_the_detail_event()
    {
        // cloud-init's own HyperVKvpReportingHandler already wrote events under
        // THIS boot's incarnation. egs must reuse it so the host decoder (highest
        // incarnation wins) renders both sets, not just one.
        var reader = new StubReader(Running("error"));
        var kvp = new RecordingDataExchange();
        kvp.Seed("CLOUD_INIT|1730000000|finish|modules-final|abc", "{}");

        await Watcher(reader, kvp, bootIncarnation: 1730000000)
            .WatchAsync(CancellationToken.None);

        kvp.Writes.SelectMany(w => w)
            .First(kv => kv.Key.StartsWith("CLOUD_INIT|", StringComparison.Ordinal) && kv.Value != null)
            .Key.Should().StartWith("CLOUD_INIT|1730000000|");
    }

    [Fact]
    public async Task WatchAsync_ignores_and_sweeps_a_prior_boot_incarnation()
    {
        // A persisted pool from an earlier boot must NOT be reused (it would make
        // the host latch onto stale data) and must be swept.
        var reader = new StubReader(Running("error"));
        var kvp = new RecordingDataExchange();
        kvp.Seed("CLOUD_INIT|1000000000|finish|modules-final/cloud-init-errors|old", "{}");

        await Watcher(reader, kvp, bootIncarnation: 2000000000)
            .WatchAsync(CancellationToken.None);

        var cloudInitWrites = kvp.Writes.SelectMany(w => w)
            .Where(kv => kv.Key.StartsWith("CLOUD_INIT|", StringComparison.Ordinal)).ToList();
        // New event under the current boot incarnation...
        cloudInitWrites.Should().Contain(kv => kv.Key.StartsWith("CLOUD_INIT|2000000000|") && kv.Value != null);
        // ...and the stale prior-boot entry deleted (null value).
        cloudInitWrites.Should().Contain(kv => kv.Key == "CLOUD_INIT|1000000000|finish|modules-final/cloud-init-errors|old" && kv.Value == null);
    }

    [Fact]
    public async Task WatchAsync_uses_the_boot_incarnation_when_no_cloud_init_entries_exist()
    {
        var reader = new StubReader(Running("error"));
        var kvp = new RecordingDataExchange();

        await Watcher(reader, kvp, bootIncarnation: 1730000500)
            .WatchAsync(CancellationToken.None);

        kvp.Writes.SelectMany(w => w)
            .First(kv => kv.Key.StartsWith("CLOUD_INIT|", StringComparison.Ordinal))
            .Key.Should().StartWith("CLOUD_INIT|1730000500|");
    }

    [Fact]
    public async Task WatchAsync_does_not_re_emit_the_detail_when_already_present_for_this_boot()
    {
        // A restart re-enters with status still "error"; the detail must not be
        // duplicated under the same incarnation.
        var reader = new StubReader(Running("error"));
        var kvp = new RecordingDataExchange();
        kvp.Seed("CLOUD_INIT|1730000000|finish|modules-final/cloud-init-errors|prev", "{}");

        await Watcher(reader, kvp, bootIncarnation: 1730000000)
            .WatchAsync(CancellationToken.None);

        kvp.Writes.SelectMany(w => w)
            .Should().NotContain(kv => kv.Key.StartsWith("CLOUD_INIT|", StringComparison.Ordinal) && kv.Value != null);
    }

    [Fact]
    public async Task WatchAsync_emits_no_detail_event_on_a_successful_run()
    {
        var reader = new StubReader(Running("done"));
        var kvp = new RecordingDataExchange();

        await Watcher(reader, kvp).WatchAsync(CancellationToken.None);

        kvp.Writes.SelectMany(w => w)
            .Should().NotContain(kv => kv.Key.StartsWith("CLOUD_INIT|", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WatchAsync_still_writes_failed_when_the_detail_emit_throws()
    {
        // A failure while producing the detail (here: the guest-data read it does
        // first) must not abort the watcher nor block the state write.
        var reader = new StubReader(Running("error"));
        var kvp = new RecordingDataExchange { ThrowOnGet = true };

        await Watcher(reader, kvp).WatchAsync(CancellationToken.None);

        kvp.WrittenStates.Should().Contain("failed");
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

    [Fact]
    public async Task WatchAsync_retries_a_failed_terminal_write_before_stopping()
    {
        // The terminal "done" write fails once. The watcher must NOT stop on the
        // failed write (which would leave the host on a stale non-terminal
        // state) — it retries until the terminal state is actually written.
        var reader = new StubReader(Running("done"), Running("done"));
        var kvp = new FlakyDataExchange(failFirst: true);

        await Watcher(reader, kvp).WatchAsync(CancellationToken.None);

        kvp.Attempts.Should().Equal("completed", "completed");
    }

    private static CloudInitProbe Running(string? status) => CloudInitProbe.Running(status);

    private static CloudInitStatusWatcher Watcher(
        ICloudInitStatusReader reader,
        IGuestDataExchange kvp,
        long? bootIncarnation = null) =>
        new(reader, kvp, NullLogger<CloudInitStatusWatcher>.Instance, TimeSpan.Zero, bootIncarnation);

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
        private readonly Dictionary<string, string> _guestData = new(StringComparer.Ordinal);

        public List<IReadOnlyDictionary<string, string?>> Writes { get; } = [];

        // When set, GetGuestDataAsync throws — simulates a transient KVP read
        // failure inside the detail-emit path.
        public bool ThrowOnGet { get; init; }

        public IEnumerable<string?> WrittenStates =>
            Writes.Select(w => w.TryGetValue("eryph.provisioning.state", out var v) ? v : null);

        // Pre-populate the guest pool (e.g. cloud-init's own CLOUD_INIT entries).
        public void Seed(string key, string value) => _guestData[key] = value;

        public Task<IReadOnlyDictionary<string, string>> GetExternalDataAsync()
            => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());

        public Task<IReadOnlyDictionary<string, string>> GetGuestDataAsync()
            => ThrowOnGet
                ? throw new IOException("kvp read failed")
                : Task.FromResult<IReadOnlyDictionary<string, string>>(
                    new Dictionary<string, string>(_guestData, StringComparer.Ordinal));

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
