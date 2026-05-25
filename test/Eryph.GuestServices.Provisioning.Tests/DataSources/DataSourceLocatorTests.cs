using AwesomeAssertions;
using Eryph.GuestServices.Provisioning.DataSources;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Eryph.GuestServices.Provisioning.Tests.DataSources;

public class DataSourceLocatorTests
{
    private static DataSourceResult MakeResult(string source) => new()
    {
        SourceName = source,
        InstanceId = $"id-{source}",
    };

    private static DataSourceLocator NewLocator(
        IEnumerable<IDataSource> sources,
        TimeSpan? maxWait = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        return new DataSourceLocator(
            sources,
            NullLogger<DataSourceLocator>.Instance,
            maxWait ?? TimeSpan.FromMinutes(10),
            delay ?? ((_, _) => Task.CompletedTask));
    }

    private static IDataSource MakeSource(
        string name,
        int priority,
        DataSourceProbeResult result,
        bool requiresNetwork = false)
    {
        var src = Substitute.For<IDataSource>();
        src.Name.Returns(name);
        src.Priority.Returns(priority);
        src.RequiresNetwork.Returns(requiresNetwork);
        src.ProbeAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(result));
        src.OnCompletedAsync(Arg.Any<DataSourceResult>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        return src;
    }

    [Fact]
    public async Task LocateAsync_returns_null_when_all_sources_are_not_applicable()
    {
        var sources = new[]
        {
            MakeSource("A", 10, DataSourceProbeResult.NotApplicable.Instance),
            MakeSource("B", 20, DataSourceProbeResult.NotApplicable.Instance),
        };

        var locator = NewLocator(sources);

        var result = await locator.LocateAsync(CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task LocateAsync_returns_first_ready_result_in_priority_order()
    {
        // Registration order is intentionally reversed against priority to prove
        // the locator sorts by Priority and not enumeration order.
        var lowPrio = MakeSource("LowPrio", 100, new DataSourceProbeResult.Ready(MakeResult("LowPrio")));
        var highPrio = MakeSource("HighPrio", 10, new DataSourceProbeResult.Ready(MakeResult("HighPrio")));

        var locator = NewLocator([lowPrio, highPrio]);

        var result = await locator.LocateAsync(CancellationToken.None);

        result.Should().NotBeNull();
        result!.SourceName.Should().Be("HighPrio");
    }

    [Fact]
    public async Task LocateAsync_skips_failed_sources_and_continues()
    {
        var failed = MakeSource("Failed", 10, new DataSourceProbeResult.Failed("boom"));
        var ready = MakeSource("Ready", 20, new DataSourceProbeResult.Ready(MakeResult("Ready")));

        var locator = NewLocator([failed, ready]);

        var result = await locator.LocateAsync(CancellationToken.None);

        result.Should().NotBeNull();
        result!.SourceName.Should().Be("Ready");
    }

    [Fact]
    public async Task LocateAsync_skips_sources_that_throw_during_probe()
    {
        var thrower = Substitute.For<IDataSource>();
        thrower.Name.Returns("Throws");
        thrower.Priority.Returns(10);
        thrower.ProbeAsync(Arg.Any<CancellationToken>())
            .Returns<Task<DataSourceProbeResult>>(_ => throw new InvalidOperationException("kaboom"));

        var ready = MakeSource("Ready", 20, new DataSourceProbeResult.Ready(MakeResult("Ready")));

        var locator = NewLocator([thrower, ready]);

        var result = await locator.LocateAsync(CancellationToken.None);

        result!.SourceName.Should().Be("Ready");
    }

    [Fact]
    public async Task LocateAsync_retries_WaitForReady_until_ready()
    {
        var src = Substitute.For<IDataSource>();
        src.Name.Returns("Slow");
        src.Priority.Returns(10);
        src.ProbeAsync(Arg.Any<CancellationToken>()).Returns(
            Task.FromResult<DataSourceProbeResult>(
                new DataSourceProbeResult.WaitForReady("not ready", TimeSpan.FromMilliseconds(1))),
            Task.FromResult<DataSourceProbeResult>(
                new DataSourceProbeResult.WaitForReady("still not ready", TimeSpan.FromMilliseconds(1))),
            Task.FromResult<DataSourceProbeResult>(
                new DataSourceProbeResult.Ready(MakeResult("Slow"))));

        var locator = NewLocator([src]);

        var result = await locator.LocateAsync(CancellationToken.None);

        result.Should().NotBeNull();
        result!.SourceName.Should().Be("Slow");
        await src.Received(3).ProbeAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LocateAsync_caps_WaitForReady_at_total_timeout()
    {
        var src = Substitute.For<IDataSource>();
        src.Name.Returns("Forever");
        src.Priority.Returns(10);
        // Always reports WaitForReady, so the locator should give up once it
        // has waited more than the configured cap.
        src.ProbeAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<DataSourceProbeResult>(
            new DataSourceProbeResult.WaitForReady("never", TimeSpan.FromSeconds(3))));

        // Cap of 5s means at most two retries (3 + 3 > 5) before giving up.
        var locator = NewLocator([src], maxWait: TimeSpan.FromSeconds(5));

        var result = await locator.LocateAsync(CancellationToken.None);

        result.Should().BeNull();
        // First probe + retry after first 3s wait. The second WaitForReady would
        // push the total wait to 6s, exceeding the 5s cap, so it bails before a
        // third probe call.
        await src.Received(2).ProbeAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OnProvisioningCompletedAsync_dispatches_back_to_the_source_that_produced_the_result()
    {
        var winner = MakeSource("Win", 10, new DataSourceProbeResult.Ready(MakeResult("Win")));
        var loser = MakeSource("Lose", 20, new DataSourceProbeResult.NotApplicable());

        var locator = NewLocator([winner, loser]);

        var data = await locator.LocateAsync(CancellationToken.None);
        await locator.OnProvisioningCompletedAsync(data!, CancellationToken.None);

        await winner.Received(1).OnCompletedAsync(data!, Arg.Any<CancellationToken>());
        await loser.DidNotReceive().OnCompletedAsync(Arg.Any<DataSourceResult>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OnProvisioningCompletedAsync_is_a_noop_for_unknown_results()
    {
        var src = MakeSource("S", 10, DataSourceProbeResult.NotApplicable.Instance);
        var locator = NewLocator([src]);

        var stranger = MakeResult("unknown");
        var act = async () => await locator.OnProvisioningCompletedAsync(stranger, CancellationToken.None);

        await act.Should().NotThrowAsync();
        await src.DidNotReceive().OnCompletedAsync(Arg.Any<DataSourceResult>(), Arg.Any<CancellationToken>());
    }

    // ---- RFC 0004 regressions ----

    [Fact]
    public async Task LocateAsync_returns_NoDataSource_when_only_WaitForReady_source_exhausts_budget()
    {
        // Regression: prior to RFC 0004 the locator iterated sources sequentially
        // and treated a perpetually-WaitForReady source as "ready of itself" once
        // the cap fired. With the new interleaved loop, an unsettled source must
        // produce null (NoDataSource), not a phantom Ready.
        var slow = Substitute.For<IDataSource>();
        slow.Name.Returns("Slow");
        slow.Priority.Returns(10);
        slow.ProbeAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<DataSourceProbeResult>(
                new DataSourceProbeResult.WaitForReady("never settles", TimeSpan.FromSeconds(1))));

        var locator = NewLocator([slow], maxWait: TimeSpan.FromMilliseconds(50));

        var result = await locator.LocateAsync(CancellationToken.None);

        result.Should().BeNull("an unsettled source must not be reported as Ready when the budget expires");
    }

    [Fact]
    public async Task LocateAsync_falls_through_to_lower_priority_when_higher_priority_is_WaitForReady()
    {
        // Regression: prior implementation blocked on the highest-priority
        // WaitForReady source until its per-source cap fired before even probing
        // the lower-priority source. The new locator interleaves so a Ready from
        // a lower-priority source wins immediately while the higher-priority one
        // is still backing off. We MUST still see the higher-priority source
        // probed at least once before returning the lower-priority Ready.
        var highPriority = Substitute.For<IDataSource>();
        highPriority.Name.Returns("HighPrioWait");
        highPriority.Priority.Returns(10);
        highPriority.ProbeAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<DataSourceProbeResult>(
                new DataSourceProbeResult.WaitForReady("not yet", TimeSpan.FromSeconds(30))));

        var lowPriority = MakeSource("LowPrioReady", 20,
            new DataSourceProbeResult.Ready(MakeResult("LowPrioReady")));

        var locator = NewLocator([highPriority, lowPriority]);

        var result = await locator.LocateAsync(CancellationToken.None);

        result.Should().NotBeNull();
        result!.SourceName.Should().Be("LowPrioReady");
        await highPriority.Received(1).ProbeAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LocateAsync_falls_through_to_lower_priority_when_higher_priority_is_Failed()
    {
        // Regression: a higher-priority source returning Failed must not abort
        // discovery; the locator continues with the next-priority source. (This
        // case existed before RFC 0004 but is restated here so the regression
        // grid for the new loop is complete.)
        var highPriority = MakeSource("HighPrioFail", 10,
            new DataSourceProbeResult.Failed("broken"));
        var lowPriority = MakeSource("LowPrioReady", 20,
            new DataSourceProbeResult.Ready(MakeResult("LowPrioReady")));

        var locator = NewLocator([highPriority, lowPriority]);

        var result = await locator.LocateAsync(CancellationToken.None);

        result!.SourceName.Should().Be("LowPrioReady");
    }

    [Fact]
    public async Task LocateAsync_mixed_Failed_high_priority_and_WaitForReady_low_priority_eventually_returns_low_priority_Ready()
    {
        // Regression: Failed + WaitForReady in the same run. The Failed source
        // is dropped immediately; the WaitForReady source must keep being
        // retried until it reports Ready, even though it's lower-priority than
        // the Failed one. Prior behaviour would short-circuit on the first
        // priority-ordered source.
        var failed = MakeSource("HighPrioFail", 10, new DataSourceProbeResult.Failed("dead"));

        var lowPriority = Substitute.For<IDataSource>();
        lowPriority.Name.Returns("LowPrioWait");
        lowPriority.Priority.Returns(20);
        lowPriority.ProbeAsync(Arg.Any<CancellationToken>()).Returns(
            Task.FromResult<DataSourceProbeResult>(
                new DataSourceProbeResult.WaitForReady("warming up", TimeSpan.FromMilliseconds(1))),
            Task.FromResult<DataSourceProbeResult>(
                new DataSourceProbeResult.Ready(MakeResult("LowPrioWait"))));

        var locator = NewLocator([failed, lowPriority]);

        var result = await locator.LocateAsync(CancellationToken.None);

        result!.SourceName.Should().Be("LowPrioWait");
        await failed.Received(1).ProbeAsync(Arg.Any<CancellationToken>());
        await lowPriority.Received(2).ProbeAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LocateAsync_returns_higher_priority_Ready_even_if_lower_priority_is_also_Ready()
    {
        // Regression: the new interleaved loop must still honour priority order
        // when *both* a high- and a low-priority source are immediately Ready.
        // The high-priority probe is sequenced before the low one within the
        // same pass.
        var highPriority = MakeSource("HighPrio", 10, new DataSourceProbeResult.Ready(MakeResult("HighPrio")));
        var lowPriority = MakeSource("LowPrio", 20, new DataSourceProbeResult.Ready(MakeResult("LowPrio")));

        var locator = NewLocator([lowPriority, highPriority]);

        var result = await locator.LocateAsync(CancellationToken.None);

        result!.SourceName.Should().Be("HighPrio");
        // We must NOT probe the lower-priority source once the higher one wins,
        // otherwise OnCompletedAsync mapping could attribute to the wrong source.
        await lowPriority.DidNotReceive().ProbeAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LocateAsync_cancellation_during_WaitForReady_backoff_aborts_immediately()
    {
        var src = Substitute.For<IDataSource>();
        src.Name.Returns("Slow");
        src.Priority.Returns(10);
        src.ProbeAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<DataSourceProbeResult>(
                new DataSourceProbeResult.WaitForReady("forever", TimeSpan.FromSeconds(10))));

        using var cts = new CancellationTokenSource();

        // Test delay that observes the cancellation token. The production
        // Task.Delay does the same; we model it here so the locator's cancel
        // contract is testable without a real wall-clock wait.
        Func<TimeSpan, CancellationToken, Task> delay = (ts, ct) => Task.Delay(ts, ct);

        var locator = new DataSourceLocator(
            [src],
            NullLogger<DataSourceLocator>.Instance,
            TimeSpan.FromMinutes(1),
            delay);

        var task = locator.LocateAsync(cts.Token);
        cts.Cancel();

        await FluentActions.Awaiting(() => task).Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task LocateAsync_exponential_backoff_grows_each_retry()
    {
        // Per RFC 0004: 1s, 2s, 4s, 8s, ... capped at MaxBackoff. We expose this
        // by capturing the actual delay values the locator passes to its sleep
        // function. Single source, always WaitForReady — so each iteration
        // records the next backoff.
        var src = Substitute.For<IDataSource>();
        src.Name.Returns("Slow");
        src.Priority.Returns(10);
        src.ProbeAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<DataSourceProbeResult>(
                new DataSourceProbeResult.WaitForReady("not yet", TimeSpan.FromSeconds(1))));

        var observedDelays = new List<TimeSpan>();
        Func<TimeSpan, CancellationToken, Task> delay = (ts, _) =>
        {
            observedDelays.Add(ts);
            return Task.CompletedTask;
        };

        var locator = new DataSourceLocator(
            [src],
            NullLogger<DataSourceLocator>.Instance,
            readinessTimeout: TimeSpan.FromSeconds(20),
            minBackoff: TimeSpan.FromSeconds(1),
            maxBackoff: TimeSpan.FromSeconds(10),
            delay: delay);

        await locator.LocateAsync(CancellationToken.None);

        // Backoffs are 1s, 2s, 4s, 8s, 10s (capped), 10s, ... — the last entry
        // could be clamped against the remaining budget so we don't pin every
        // value. We DO assert the prefix grows monotonically and is capped.
        observedDelays.Should().NotBeEmpty();
        observedDelays.Take(4).Should().BeEquivalentTo(new[]
        {
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(4),
            TimeSpan.FromSeconds(8),
        });
        observedDelays.Should().AllSatisfy(d => d.Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task LocateAsync_logs_state_transitions_at_Information_level()
    {
        // Per RFC 0004: a Ready->Failed->Ready oscillation needs to be visible
        // at Information level so the operator can tell "the source came up"
        // apart from "it keeps flapping". Verifies by capturing log records.
        var captured = new List<(LogLevel level, string message)>();
        var logger = new CapturingLogger(captured);

        var src = Substitute.For<IDataSource>();
        src.Name.Returns("Flaky");
        src.Priority.Returns(10);
        src.ProbeAsync(Arg.Any<CancellationToken>()).Returns(
            Task.FromResult<DataSourceProbeResult>(
                new DataSourceProbeResult.WaitForReady("not yet", TimeSpan.FromMilliseconds(1))),
            Task.FromResult<DataSourceProbeResult>(
                new DataSourceProbeResult.Ready(MakeResult("Flaky"))));

        var locator = new DataSourceLocator(
            [src],
            logger,
            TimeSpan.FromMinutes(1),
            (_, _) => Task.CompletedTask);

        await locator.LocateAsync(CancellationToken.None);

        captured.Should().Contain(e =>
            e.level == LogLevel.Information && e.message.Contains("state changed"));
    }

    // ---- RFC 0005 regressions ----

    [Fact]
    public async Task OnProvisioningCompletedAsync_is_idempotent_when_called_twice()
    {
        // RFC 0005: calling the hook a second time after success must NOT throw
        // and must dispatch to the same source — the source itself must handle
        // "nothing to clean up" idempotently, which the AzureDataSource tests
        // verify separately.
        var winner = MakeSource("Win", 10, new DataSourceProbeResult.Ready(MakeResult("Win")));
        var locator = NewLocator([winner]);

        var data = await locator.LocateAsync(CancellationToken.None);

        await locator.OnProvisioningCompletedAsync(data!, CancellationToken.None);
        await locator.OnProvisioningCompletedAsync(data!, CancellationToken.None);

        await winner.Received(2).OnCompletedAsync(data!, Arg.Any<CancellationToken>());
    }

    // ---- datasource_list (cloud-init parity) ----

    private static DataSourceLocator NewLocatorWithList(
        IEnumerable<IDataSource> sources,
        IReadOnlyList<string>? dataSourceList,
        ILogger<DataSourceLocator>? logger = null)
    {
        return new DataSourceLocator(
            sources,
            logger ?? NullLogger<DataSourceLocator>.Instance,
            TimeSpan.FromMinutes(10),
            DataSourceLocator.DefaultMinBackoff,
            DataSourceLocator.DefaultMaxBackoff,
            (_, _) => Task.CompletedTask,
            dataSourceList);
    }

    [Fact]
    public async Task LocateAsync_with_DataSourceList_probes_only_the_named_source()
    {
        var noCloud = MakeSource("NoCloud", 10, new DataSourceProbeResult.Ready(MakeResult("NoCloud")));
        var configDrive = MakeSource("ConfigDrive", 20, new DataSourceProbeResult.Ready(MakeResult("ConfigDrive")));

        var locator = NewLocatorWithList([noCloud, configDrive], ["NoCloud"]);

        var result = await locator.LocateAsync(CancellationToken.None);

        result!.SourceName.Should().Be("NoCloud");
        // ConfigDrive is not in the configured list, so it must never be probed.
        await configDrive.DidNotReceive().ProbeAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LocateAsync_with_DataSourceList_honours_listed_order_over_priority()
    {
        // ConfigDrive has the *lower* priority value (would normally lose to NoCloud),
        // but the configured list puts it first, so it must win.
        var noCloud = MakeSource("NoCloud", 10, new DataSourceProbeResult.Ready(MakeResult("NoCloud")));
        var configDrive = MakeSource("ConfigDrive", 20, new DataSourceProbeResult.Ready(MakeResult("ConfigDrive")));

        var locator = NewLocatorWithList([noCloud, configDrive], ["ConfigDrive", "NoCloud"]);

        var result = await locator.LocateAsync(CancellationToken.None);

        result!.SourceName.Should().Be("ConfigDrive");
        // ConfigDrive short-circuits the pass before NoCloud is reached.
        await noCloud.DidNotReceive().ProbeAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LocateAsync_with_DataSourceList_matches_case_insensitively()
    {
        var noCloud = MakeSource("NoCloud", 10, new DataSourceProbeResult.Ready(MakeResult("NoCloud")));
        var configDrive = MakeSource("ConfigDrive", 20, new DataSourceProbeResult.Ready(MakeResult("ConfigDrive")));

        var locator = NewLocatorWithList([noCloud, configDrive], ["nocloud"]);

        var result = await locator.LocateAsync(CancellationToken.None);

        result!.SourceName.Should().Be("NoCloud");
        await configDrive.DidNotReceive().ProbeAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LocateAsync_with_DataSourceList_logs_warning_for_unknown_name_but_still_probes_known()
    {
        var captured = new List<(LogLevel level, string message)>();
        var logger = new CapturingLogger(captured);

        var noCloud = MakeSource("NoCloud", 10, new DataSourceProbeResult.Ready(MakeResult("NoCloud")));
        var configDrive = MakeSource("ConfigDrive", 20, DataSourceProbeResult.NotApplicable.Instance);

        var locator = NewLocatorWithList([noCloud, configDrive], ["NoCloud", "Bogus"], logger);

        var result = await locator.LocateAsync(CancellationToken.None);

        result!.SourceName.Should().Be("NoCloud");
        captured.Should().Contain(e =>
            e.level == LogLevel.Warning && e.message.Contains("Bogus"));
    }

    [Fact]
    public async Task LocateAsync_with_only_unknown_names_falls_back_to_all_by_priority()
    {
        var captured = new List<(LogLevel level, string message)>();
        var logger = new CapturingLogger(captured);

        // Reversed registration order proves the fallback still sorts by Priority.
        var lowPrio = MakeSource("LowPrio", 100, new DataSourceProbeResult.Ready(MakeResult("LowPrio")));
        var highPrio = MakeSource("HighPrio", 10, new DataSourceProbeResult.Ready(MakeResult("HighPrio")));

        var locator = NewLocatorWithList([lowPrio, highPrio], ["Nope", "AlsoNope"], logger);

        var result = await locator.LocateAsync(CancellationToken.None);

        // Falls back to all-by-Priority, so the highest-priority source wins.
        result!.SourceName.Should().Be("HighPrio");
        captured.Should().Contain(e =>
            e.level == LogLevel.Warning && e.message.Contains("falling back"));
    }

    [Fact]
    public async Task LocateAsync_with_null_DataSourceList_uses_all_sources_by_priority()
    {
        // Reversed registration order proves the default path still sorts by Priority.
        var lowPrio = MakeSource("LowPrio", 100, new DataSourceProbeResult.Ready(MakeResult("LowPrio")));
        var highPrio = MakeSource("HighPrio", 10, new DataSourceProbeResult.Ready(MakeResult("HighPrio")));

        var locator = NewLocatorWithList([lowPrio, highPrio], dataSourceList: null);

        var result = await locator.LocateAsync(CancellationToken.None);

        result!.SourceName.Should().Be("HighPrio");
    }

    [Fact]
    public async Task LocateAsync_with_empty_DataSourceList_uses_all_sources_by_priority()
    {
        var lowPrio = MakeSource("LowPrio", 100, new DataSourceProbeResult.Ready(MakeResult("LowPrio")));
        var highPrio = MakeSource("HighPrio", 10, new DataSourceProbeResult.Ready(MakeResult("HighPrio")));

        var locator = NewLocatorWithList([lowPrio, highPrio], dataSourceList: []);

        var result = await locator.LocateAsync(CancellationToken.None);

        result!.SourceName.Should().Be("HighPrio");
    }

    private sealed class CapturingLogger(List<(LogLevel level, string message)> sink)
        : ILogger<DataSourceLocator>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            sink.Add((logLevel, formatter(state, exception)));
        }
    }
}
