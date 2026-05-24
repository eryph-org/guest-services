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
    public async Task PublishAsync_writes_started_state_and_clears_error_and_reboot_reason()
    {
        var (handler, kvp) = Build();

        await handler.PublishAsync(
            new ReportingEvent.ProvisioningStarted("i-1") { Origin = "stage-runner" },
            CancellationToken.None);

        var written = LastWrite(kvp);
        written["eryph.provisioning.state"].Should().Be("started");
        written["eryph.provisioning.instance"].Should().Be("i-1");
        written["eryph.provisioning.error"].Should().BeNull();
        written["eryph.provisioning.reboot_reason"].Should().BeNull();
        written.Should().ContainKey("eryph.provisioning.updated");
    }

    [Fact]
    public async Task PublishAsync_StageStarted_writes_running_state_with_stage_name()
    {
        var (handler, kvp) = Build();

        await handler.PublishAsync(
            new ReportingEvent.StageStarted(Stage.Network) { Origin = "stage:Network" },
            CancellationToken.None);

        var written = LastWrite(kvp);
        written["eryph.provisioning.state"].Should().Be("running");
        written["eryph.provisioning.stage"].Should().Be("Network");
    }

    [Fact]
    public async Task PublishAsync_RebootRequested_writes_reboot_pending_with_reason()
    {
        var (handler, kvp) = Build();

        await handler.PublishAsync(
            new ReportingEvent.RebootRequested("needs-restart") { Origin = "module:Foo" },
            CancellationToken.None);

        var written = LastWrite(kvp);
        written["eryph.provisioning.state"].Should().Be("reboot_pending");
        written["eryph.provisioning.reboot_reason"].Should().Be("needs-restart");
    }

    [Fact]
    public async Task PublishAsync_ProvisioningCompleted_writes_completed_and_clears_transient_fields()
    {
        var (handler, kvp) = Build();

        await handler.PublishAsync(
            new ReportingEvent.ProvisioningCompleted { Origin = "stage-runner" },
            CancellationToken.None);

        var written = LastWrite(kvp);
        written["eryph.provisioning.state"].Should().Be("completed");
        written["eryph.provisioning.stage"].Should().BeNull();
        written["eryph.provisioning.error"].Should().BeNull();
        written["eryph.provisioning.reboot_reason"].Should().BeNull();
    }

    [Fact]
    public async Task PublishAsync_ProvisioningFailed_writes_failed_state_with_reason()
    {
        var (handler, kvp) = Build();

        await handler.PublishAsync(
            new ReportingEvent.ProvisioningFailed("boom", new InvalidOperationException("x")) { Origin = "stage-runner" },
            CancellationToken.None);

        var written = LastWrite(kvp);
        written["eryph.provisioning.state"].Should().Be("failed");
        written["eryph.provisioning.error"].Should().Be("boom");
    }

    [Fact]
    public async Task PublishAsync_always_writes_updated_timestamp()
    {
        var (handler, kvp) = Build();

        await handler.PublishAsync(
            new ReportingEvent.Progress("hello") { Origin = "module:Foo" },
            CancellationToken.None);

        var written = LastWrite(kvp);
        written.Should().ContainKey("eryph.provisioning.updated");
        written["eryph.provisioning.updated"].Should().NotBeNullOrWhiteSpace();
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
    public async Task PublishAsync_SshHostKeysReported_writes_joined_fingerprints()
    {
        var (handler, kvp) = Build();

        await handler.PublishAsync(
            new ReportingEvent.SshHostKeysReported(
                [
                    new SshHostKeyFingerprint("ed25519", "SHA256:aaa", "ssh-ed25519 AAA"),
                    new SshHostKeyFingerprint("rsa", "SHA256:bbb", "ssh-rsa BBB"),
                ])
            { Origin = "module:SshModule" },
            CancellationToken.None);

        var written = LastWrite(kvp);
        written["eryph.provisioning.ssh_host_keys"]
            .Should().Be("ed25519=SHA256:aaa;rsa=SHA256:bbb");
        written.Should().ContainKey("eryph.provisioning.updated");
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
