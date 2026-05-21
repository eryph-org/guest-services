using AwesomeAssertions;
using Eryph.GuestServices.Provisioning.DataSources;
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
}
