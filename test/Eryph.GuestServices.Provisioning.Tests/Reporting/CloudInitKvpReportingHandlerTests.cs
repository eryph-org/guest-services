using System.Text.Json;
using AwesomeAssertions;
using Eryph.GuestServices.HvDataExchange.Guest;
using Eryph.GuestServices.Provisioning.Reporting.Events;
using Eryph.GuestServices.Provisioning.Reporting.Handlers;
using Eryph.GuestServices.Provisioning.Semaphores;
using Eryph.GuestServices.Provisioning.Stages;
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
            kvp, BootClock(0), NullLogger<CloudInitKvpReportingHandler>.Instance);

        handler.IsApplicable.Should().BeFalse();
    }

    [Fact]
    public async Task PublishAsync_uses_the_boot_epoch_second_as_the_incarnation()
    {
        // cloud-init's incarnation is the boot time as epoch seconds; here the
        // boot clock reports a fixed boot time so the key is deterministic.
        var (handler, kvp) = Build(incarnation: 1_700_000_000);

        await handler.PublishAsync(
            new ReportingEvent.StageStarted(Stage.Local) { Origin = "stage:Local" },
            CancellationToken.None);

        var key = LastWrittenKey(kvp);
        // cloud-init _event_key: CLOUD_INIT|<incarnation>|<type>|<name>|<uuid>.
        key.Should().StartWith("CLOUD_INIT|1700000000|start|init-local|");
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
            ["CLOUD_INIT|9|finish|modules-final|old"] = "{}",     // stale incarnation
            ["CLOUD_INIT|0|start|init-local|keep"] = "{}",        // current incarnation
            ["eryph.provisioning.state"] = "running",             // foreign key, untouched
        });

        var handler = new CloudInitKvpReportingHandler(
            kvp, BootClock(0), NullLogger<CloudInitKvpReportingHandler>.Instance);
        kvp.ClearReceivedCalls();

        await handler.PublishAsync(
            new ReportingEvent.StageStarted(Stage.Local) { Origin = "stage:Local" },
            CancellationToken.None);

        var deletions = AllWrites(kvp)
            .SelectMany(w => w)
            .Where(p => p.Value is null)
            .Select(p => p.Key)
            .ToList();

        deletions.Should().ContainSingle().Which.Should().Be("CLOUD_INIT|9|finish|modules-final|old");
    }

    [Fact]
    public async Task PublishAsync_skips_the_sweep_when_the_boot_time_cannot_be_read()
    {
        // Boot time unreadable -> incarnation falls back to 0. The sweep must be
        // skipped: with the 0 fallback it cannot distinguish this boot's entries
        // from prior ones and would wipe real, current entries.
        var kvp = WorkingKvp();
        kvp.GetGuestDataAsync().Returns(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["CLOUD_INIT|1700000000|finish|modules-final|real"] = "{}",
        });
        var clock = Substitute.For<IBootClock>();
        clock.GetCurrentBootTime().Throws(new InvalidOperationException("boot time unavailable"));

        var handler = new CloudInitKvpReportingHandler(
            kvp, clock, NullLogger<CloudInitKvpReportingHandler>.Instance);
        kvp.ClearReceivedCalls();

        await handler.PublishAsync(
            new ReportingEvent.StageStarted(Stage.Local) { Origin = "stage:Local" },
            CancellationToken.None);

        AllWrites(kvp).SelectMany(w => w).Where(p => p.Value is null).Should().BeEmpty();
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
            kvp, BootClock(0), NullLogger<CloudInitKvpReportingHandler>.Instance);
        handler.IsApplicable.Should().BeTrue();

        var act = async () => await handler.PublishAsync(
            new ReportingEvent.StageStarted(Stage.Final) { Origin = "stage:Final" },
            CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task PublishAsync_emits_a_fail_finish_for_provisioning_failure()
    {
        var (handler, kvp) = Build();

        await handler.PublishAsync(
            new ReportingEvent.ProvisioningFailed("boom", null) { Origin = "stage-runner" },
            CancellationToken.None);

        using var doc = JsonDocument.Parse(LastWrittenValue(kvp));
        doc.RootElement.GetProperty("type").GetString().Should().Be("finish");
        doc.RootElement.GetProperty("result").GetString().Should().Be("FAIL");
        doc.RootElement.GetProperty("msg").GetString().Should().Be("boom");
    }

    private static (CloudInitKvpReportingHandler handler, IGuestDataExchange kvp) Build(
        long incarnation = 0)
    {
        var kvp = WorkingKvp();
        kvp.GetGuestDataAsync().Returns(new Dictionary<string, string>());
        var handler = new CloudInitKvpReportingHandler(
            kvp, BootClock(incarnation), NullLogger<CloudInitKvpReportingHandler>.Instance);
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

    private static IBootClock BootClock(long bootEpochSeconds)
    {
        var clock = Substitute.For<IBootClock>();
        clock.GetCurrentBootTime().Returns(DateTimeOffset.FromUnixTimeSeconds(bootEpochSeconds));
        return clock;
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
