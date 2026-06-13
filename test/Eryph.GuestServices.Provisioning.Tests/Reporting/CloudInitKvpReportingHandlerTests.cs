using System.Text.Json;
using AwesomeAssertions;
using Eryph.GuestServices.HvDataExchange.Guest;
using Eryph.GuestServices.Provisioning.Reporting.Events;
using Eryph.GuestServices.Provisioning.Reporting.Handlers;
using Eryph.GuestServices.Provisioning.Stages;
using Eryph.GuestServices.Provisioning.State;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Eryph.GuestServices.Provisioning.Tests.Reporting;

public sealed class CloudInitKvpReportingHandlerTests
{
    [Fact]
    public void IsApplicable_is_true_when_probe_succeeds()
    {
        var (handler, _) = Build();
        handler.IsApplicable.Should().BeTrue();
    }

    [Fact]
    public void IsApplicable_is_false_when_probe_throws()
    {
        var kvp = Substitute.For<IGuestDataExchange>();
        kvp.SetGuestValuesAsync(Arg.Any<IReadOnlyDictionary<string, string?>>())
            .Throws(new InvalidOperationException("no kvp here"));

        var handler = new CloudInitKvpReportingHandler(
            kvp, StateStore(0), VmId("vm"), NullLogger<CloudInitKvpReportingHandler>.Instance);

        handler.IsApplicable.Should().BeFalse();
    }

    [Fact]
    public async Task PublishAsync_writes_a_cloud_init_event_with_the_current_incarnation()
    {
        var (handler, kvp) = Build(rebootCount: 3, vmId: "VMID");

        await handler.PublishAsync(
            new ReportingEvent.StageStarted(Stage.Local) { Origin = "stage:Local" },
            CancellationToken.None);

        var key = LastWrittenKey(kvp);
        key.Should().StartWith("CLOUD_INIT|3|start|init-local|VMID|");
    }

    [Fact]
    public async Task PublishAsync_nests_module_events_under_the_running_stage()
    {
        var (handler, kvp) = Build();

        await handler.PublishAsync(
            new ReportingEvent.StageStarted(Stage.Config) { Origin = "stage:Config" },
            CancellationToken.None);
        await handler.PublishAsync(
            new ReportingEvent.ModuleStarted("SetHostname") { Origin = "module:SetHostname" },
            CancellationToken.None);

        LastWrittenKey(kvp).Should().Contain("|start|modules-config/SetHostname|");
    }

    [Fact]
    public async Task PublishAsync_does_not_write_events_without_a_cloud_init_analogue()
    {
        var (handler, kvp) = Build();

        await handler.PublishAsync(
            new ReportingEvent.ProvisioningStarted("i-1") { Origin = "stage-runner" },
            CancellationToken.None);

        WrittenEventKeys(kvp).Should().BeEmpty();
    }

    [Fact]
    public async Task PublishAsync_sweeps_entries_from_other_incarnations()
    {
        var kvp = WorkingKvp();
        kvp.GetGuestDataAsync().Returns(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["CLOUD_INIT|9|finish|modules-final|vm|old"] = "{}",   // stale incarnation
            ["CLOUD_INIT|0|start|init-local|vm|keep"] = "{}",      // current incarnation
            ["eryph.provisioning.state"] = "running",             // foreign key, untouched
        });

        var handler = new CloudInitKvpReportingHandler(
            kvp, StateStore(0), VmId("vm"), NullLogger<CloudInitKvpReportingHandler>.Instance);
        kvp.ClearReceivedCalls();

        await handler.PublishAsync(
            new ReportingEvent.StageStarted(Stage.Local) { Origin = "stage:Local" },
            CancellationToken.None);

        var deletions = AllWrites(kvp)
            .SelectMany(w => w)
            .Where(p => p.Value is null)
            .Select(p => p.Key)
            .ToList();

        deletions.Should().ContainSingle().Which.Should().Be("CLOUD_INIT|9|finish|modules-final|vm|old");
    }

    [Fact]
    public async Task PublishAsync_swallows_kvp_write_failures()
    {
        var kvp = Substitute.For<IGuestDataExchange>();
        var callCount = 0;
        kvp.SetGuestValuesAsync(Arg.Any<IReadOnlyDictionary<string, string?>>())
            .Returns(_ =>
            {
                callCount++;
                return callCount == 1 ? Task.CompletedTask : Task.FromException(new InvalidOperationException("gone"));
            });
        kvp.GetGuestDataAsync().Returns(new Dictionary<string, string>());

        var handler = new CloudInitKvpReportingHandler(
            kvp, StateStore(0), VmId("vm"), NullLogger<CloudInitKvpReportingHandler>.Instance);
        handler.IsApplicable.Should().BeTrue();

        var act = async () => await handler.PublishAsync(
            new ReportingEvent.StageStarted(Stage.Final) { Origin = "stage:Final" },
            CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task PublishAsync_attaches_duration_to_a_finish_paired_with_its_start()
    {
        var (handler, kvp) = Build();
        var t0 = new DateTimeOffset(2026, 6, 3, 12, 0, 0, TimeSpan.Zero);

        await handler.PublishAsync(
            new ReportingEvent.StageStarted(Stage.Network) { Origin = "stage:Network", Timestamp = t0 },
            CancellationToken.None);
        await handler.PublishAsync(
            new ReportingEvent.StageFinished(Stage.Network)
            { Origin = "stage:Network", Timestamp = t0.AddMilliseconds(1500) },
            CancellationToken.None);

        using var doc = JsonDocument.Parse(LastWrittenValue(kvp));
        doc.RootElement.GetProperty("type").GetString().Should().Be("finish");
        doc.RootElement.GetProperty("duration").GetDouble().Should().Be(1.5);
    }

    [Fact]
    public async Task PublishAsync_start_events_carry_no_duration()
    {
        var (handler, kvp) = Build();

        await handler.PublishAsync(
            new ReportingEvent.StageStarted(Stage.Local) { Origin = "stage:Local" },
            CancellationToken.None);

        using var doc = JsonDocument.Parse(LastWrittenValue(kvp));
        doc.RootElement.TryGetProperty("duration", out _).Should().BeFalse();
    }

    [Fact]
    public async Task PublishAsync_finish_without_a_matching_start_carries_no_duration()
    {
        var (handler, kvp) = Build();

        // ProvisioningFailed maps to a finish FAIL on init-local; no StageStarted
        // was published, so there is no start to pair with.
        await handler.PublishAsync(
            new ReportingEvent.ProvisioningFailed("boom", null) { Origin = "stage-runner" },
            CancellationToken.None);

        using var doc = JsonDocument.Parse(LastWrittenValue(kvp));
        doc.RootElement.GetProperty("result").GetString().Should().Be("FAIL");
        doc.RootElement.TryGetProperty("duration", out _).Should().BeFalse();
    }

    private static (CloudInitKvpReportingHandler handler, IGuestDataExchange kvp) Build(
        int rebootCount = 0, string vmId = "vm")
    {
        var kvp = WorkingKvp();
        kvp.GetGuestDataAsync().Returns(new Dictionary<string, string>());
        var handler = new CloudInitKvpReportingHandler(
            kvp, StateStore(rebootCount), VmId(vmId), NullLogger<CloudInitKvpReportingHandler>.Instance);
        // Drop the probe write so assertions see only publish payloads.
        kvp.ClearReceivedCalls();
        return (handler, kvp);
    }

    private static IGuestDataExchange WorkingKvp()
    {
        var kvp = Substitute.For<IGuestDataExchange>();
        kvp.SetGuestValuesAsync(Arg.Any<IReadOnlyDictionary<string, string?>>())
            .Returns(Task.CompletedTask);
        return kvp;
    }

    private static IStateStore StateStore(int rebootCount)
    {
        var store = Substitute.For<IStateStore>();
        store.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(new ProvisioningState { RebootCount = rebootCount });
        return store;
    }

    private static IVmIdProvider VmId(string value)
    {
        var provider = Substitute.For<IVmIdProvider>();
        provider.GetVmId().Returns(value);
        return provider;
    }

    private static List<IReadOnlyDictionary<string, string?>> AllWrites(IGuestDataExchange kvp) =>
        kvp.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IGuestDataExchange.SetGuestValuesAsync))
            .Select(c => (IReadOnlyDictionary<string, string?>)c.GetArguments()[0]!)
            .ToList();

    private static List<string> WrittenEventKeys(IGuestDataExchange kvp) =>
        AllWrites(kvp)
            .SelectMany(w => w)
            .Where(p => p.Value is not null && p.Key.StartsWith("CLOUD_INIT|", StringComparison.Ordinal))
            .Select(p => p.Key)
            .ToList();

    private static string LastWrittenKey(IGuestDataExchange kvp)
    {
        var keys = WrittenEventKeys(kvp);
        keys.Should().NotBeEmpty();
        return keys[^1];
    }

    private static string LastWrittenValue(IGuestDataExchange kvp)
    {
        var values = AllWrites(kvp)
            .SelectMany(w => w)
            .Where(p => p.Value is not null && p.Key.StartsWith("CLOUD_INIT|", StringComparison.Ordinal))
            .Select(p => p.Value!)
            .ToList();
        values.Should().NotBeEmpty();
        return values[^1];
    }
}
