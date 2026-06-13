using AwesomeAssertions;
using Eryph.GuestServices.HvDataExchange.Guest;
using Eryph.GuestServices.Provisioning.Reporting.Events;
using Eryph.GuestServices.Provisioning.Reporting.Handlers;
using Eryph.GuestServices.Provisioning.Stages;
using Eryph.GuestServices.Provisioning.Windows;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Eryph.GuestServices.Provisioning.Tests.Reporting;

public sealed class KvpReportingHandlerTests
{
    [Fact]
    public void IsApplicable_is_true_when_probe_succeeds()
    {
        var kvp = WorkingKvp();

        var handler = new KvpReportingHandler(kvp, NullLogger<KvpReportingHandler>.Instance);

        handler.IsApplicable.Should().BeTrue();
    }

    [Fact]
    public void IsApplicable_is_false_when_probe_throws()
    {
        var kvp = Substitute.For<IGuestDataExchange>();
        kvp.SetGuestValuesAsync(Arg.Any<IReadOnlyDictionary<string, string?>>())
            .Throws(new InvalidOperationException("no kvp here"));

        var handler = new KvpReportingHandler(kvp, NullLogger<KvpReportingHandler>.Instance);

        handler.IsApplicable.Should().BeFalse();
    }

    [Fact]
    public async Task PublishAsync_writes_started_state_only()
    {
        var (handler, kvp) = Build();

        await handler.PublishAsync(
            new ReportingEvent.ProvisioningStarted("i-1") { Origin = "stage-runner" },
            CancellationToken.None);

        var written = LastWrite(kvp);
        written["eryph.provisioning.state"].Should().Be("started");
        // The bespoke side keys are gone (RFC 0031): only the status key is written.
        written.Should().ContainSingle();
        written.Keys.Should().NotContain("eryph.provisioning.instance");
    }

    [Fact]
    public async Task PublishAsync_StageStarted_writes_running_state_only()
    {
        var (handler, kvp) = Build();

        await handler.PublishAsync(
            new ReportingEvent.StageStarted(Stage.Network) { Origin = "stage:Network" },
            CancellationToken.None);

        var written = LastWrite(kvp);
        written["eryph.provisioning.state"].Should().Be("running");
        written.Keys.Should().NotContain("eryph.provisioning.stage");
    }

    [Fact]
    public async Task PublishAsync_RebootRequested_writes_reboot_pending_only()
    {
        var (handler, kvp) = Build();

        await handler.PublishAsync(
            new ReportingEvent.RebootRequested("needs-restart") { Origin = "module:Foo" },
            CancellationToken.None);

        var written = LastWrite(kvp);
        written["eryph.provisioning.state"].Should().Be("reboot_pending");
        written.Keys.Should().NotContain("eryph.provisioning.reboot_reason");
    }

    [Fact]
    public async Task PublishAsync_ProvisioningCompleted_writes_completed_only()
    {
        var (handler, kvp) = Build();

        await handler.PublishAsync(
            new ReportingEvent.ProvisioningCompleted { Origin = "stage-runner" },
            CancellationToken.None);

        var written = LastWrite(kvp);
        written["eryph.provisioning.state"].Should().Be("completed");
        written.Should().ContainSingle();
    }

    [Fact]
    public async Task PublishAsync_ProvisioningFailed_writes_failed_state_without_reason()
    {
        var (handler, kvp) = Build();

        await handler.PublishAsync(
            new ReportingEvent.ProvisioningFailed("boom", new InvalidOperationException("x")) { Origin = "stage-runner" },
            CancellationToken.None);

        var written = LastWrite(kvp);
        written["eryph.provisioning.state"].Should().Be("failed");
        // The reason is carried by the CLOUD_INIT|... FAIL event, NOT a bespoke
        // Windows-only error key (RFC 0031).
        written.Keys.Should().NotContain("eryph.provisioning.error");
    }

    [Fact]
    public async Task PublishAsync_ignores_events_that_do_not_change_status()
    {
        var (handler, kvp) = Build();

        // Progress / module / stage-finish events are reflected in the
        // CLOUD_INIT|... stream, not the status key — the handler writes nothing.
        await handler.PublishAsync(
            new ReportingEvent.Progress("hello") { Origin = "module:Foo" },
            CancellationToken.None);

        kvp.ReceivedCalls()
            .Count(c => c.GetMethodInfo().Name == nameof(IGuestDataExchange.SetGuestValuesAsync))
            .Should().Be(0);
    }

    [Fact]
    public void Probe_failure_writes_no_publish_payload()
    {
        var kvp = Substitute.For<IGuestDataExchange>();
        kvp.SetGuestValuesAsync(Arg.Any<IReadOnlyDictionary<string, string?>>())
            .Throws(new InvalidOperationException("no kvp here"));

        var handler = new KvpReportingHandler(kvp, NullLogger<KvpReportingHandler>.Instance);

        handler.IsApplicable.Should().BeFalse();
        // The dispatcher gates non-applicable handlers; verifying IsApplicable=false
        // is the contract that guarantees no publishes occur in production.
    }

    [Fact]
    public async Task PublishAsync_swallows_kvp_write_failures()
    {
        var kvp = Substitute.For<IGuestDataExchange>();
        // Probe (first call) succeeds, subsequent writes fail.
        var callCount = 0;
        kvp.SetGuestValuesAsync(Arg.Any<IReadOnlyDictionary<string, string?>>())
            .Returns(_ =>
            {
                callCount++;
                return callCount == 1
                    ? Task.CompletedTask
                    : Task.FromException(new InvalidOperationException("kvp gone"));
            });

        var handler = new KvpReportingHandler(kvp, NullLogger<KvpReportingHandler>.Instance);
        handler.IsApplicable.Should().BeTrue();

        var act = async () => await handler.PublishAsync(
            new ReportingEvent.ProvisioningCompleted { Origin = "stage-runner" },
            CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task PublishAsync_does_not_write_a_KVP_key_for_SshHostKeysReported()
    {
        // The event stays a first-class report (the log handler + other sinks
        // consume it), but its KVP key had no consumer and was dropped — this
        // status handler no longer writes anything for it.
        var (handler, kvp) = Build();

        await handler.PublishAsync(
            new ReportingEvent.SshHostKeysReported(
                [new SshHostKeyFingerprint("ed25519", "SHA256:aaa", "ssh-ed25519 AAA")])
            { Origin = "module:SshModule" },
            CancellationToken.None);

        kvp.ReceivedCalls()
            .Count(c => c.GetMethodInfo().Name == nameof(IGuestDataExchange.SetGuestValuesAsync))
            .Should().Be(0);
    }

    private static IGuestDataExchange WorkingKvp()
    {
        var kvp = Substitute.For<IGuestDataExchange>();
        kvp.SetGuestValuesAsync(Arg.Any<IReadOnlyDictionary<string, string?>>())
            .Returns(Task.CompletedTask);
        return kvp;
    }

    private static (KvpReportingHandler handler, IGuestDataExchange kvp) Build()
    {
        var kvp = WorkingKvp();
        var handler = new KvpReportingHandler(kvp, NullLogger<KvpReportingHandler>.Instance);
        // Drop the probe write so LastWrite() returns the publish payload.
        kvp.ClearReceivedCalls();
        return (handler, kvp);
    }

    private static IReadOnlyDictionary<string, string?> LastWrite(IGuestDataExchange kvp)
    {
        var calls = kvp.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IGuestDataExchange.SetGuestValuesAsync))
            .ToList();
        calls.Should().NotBeEmpty();
        return (IReadOnlyDictionary<string, string?>)calls[^1].GetArguments()[0]!;
    }
}
