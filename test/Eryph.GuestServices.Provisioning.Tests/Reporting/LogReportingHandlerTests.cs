using AwesomeAssertions;
using Eryph.GuestServices.Provisioning.Reporting.Events;
using Eryph.GuestServices.Provisioning.Reporting.Handlers;
using Eryph.GuestServices.Provisioning.Stages;
using Microsoft.Extensions.Logging;

namespace Eryph.GuestServices.Provisioning.Tests.Reporting;

public sealed class LogReportingHandlerTests
{
    [Fact]
    public void IsApplicable_is_always_true()
    {
        var handler = new LogReportingHandler(new CapturingLogger<LogReportingHandler>());
        handler.IsApplicable.Should().BeTrue();
    }

    [Fact]
    public async Task ProvisioningStarted_logs_Information_with_instance_id()
    {
        var (handler, log) = Build();

        await handler.PublishAsync(
            new ReportingEvent.ProvisioningStarted("i-1") { Origin = "stage-runner" },
            CancellationToken.None);

        var entry = log.Entries.Should().ContainSingle().Subject;
        entry.Level.Should().Be(LogLevel.Information);
        entry.Message.Should().Contain("provisioning started").And.Contain("i-1");
    }

    [Fact]
    public async Task StageStarted_logs_Information()
    {
        var (handler, log) = Build();

        await handler.PublishAsync(
            new ReportingEvent.StageStarted(Stage.Network) { Origin = "stage:Network" },
            CancellationToken.None);

        var entry = log.Entries.Should().ContainSingle().Subject;
        entry.Level.Should().Be(LogLevel.Information);
        entry.Message.Should().Contain("Network");
    }

    [Fact]
    public async Task StageFinished_logs_Debug()
    {
        var (handler, log) = Build();

        await handler.PublishAsync(
            new ReportingEvent.StageFinished(Stage.Config) { Origin = "stage:Config" },
            CancellationToken.None);

        log.Entries.Should().ContainSingle().Which.Level.Should().Be(LogLevel.Debug);
    }

    [Fact]
    public async Task ModuleStarted_logs_Information()
    {
        var (handler, log) = Build();

        await handler.PublishAsync(
            new ReportingEvent.ModuleStarted("FooModule") { Origin = "module:FooModule" },
            CancellationToken.None);

        var entry = log.Entries.Should().ContainSingle().Subject;
        entry.Level.Should().Be(LogLevel.Information);
        entry.Message.Should().Contain("FooModule");
    }

    [Fact]
    public async Task ModuleFinished_logs_Debug_with_outcome()
    {
        var (handler, log) = Build();

        await handler.PublishAsync(
            new ReportingEvent.ModuleFinished("FooModule", "Completed") { Origin = "module:FooModule" },
            CancellationToken.None);

        var entry = log.Entries.Should().ContainSingle().Subject;
        entry.Level.Should().Be(LogLevel.Debug);
        entry.Message.Should().Contain("Completed");
    }

    [Fact]
    public async Task ModuleFailed_logs_Error_with_exception()
    {
        var (handler, log) = Build();
        var ex = new InvalidOperationException("boom");

        await handler.PublishAsync(
            new ReportingEvent.ModuleFailed("FooModule", "broken", ex) { Origin = "module:FooModule" },
            CancellationToken.None);

        var entry = log.Entries.Should().ContainSingle().Subject;
        entry.Level.Should().Be(LogLevel.Error);
        entry.Exception.Should().BeSameAs(ex);
        entry.Message.Should().Contain("broken");
    }

    [Fact]
    public async Task RebootRequested_logs_Information_with_reason()
    {
        var (handler, log) = Build();

        await handler.PublishAsync(
            new ReportingEvent.RebootRequested("needs-restart") { Origin = "module:FooModule" },
            CancellationToken.None);

        var entry = log.Entries.Should().ContainSingle().Subject;
        entry.Level.Should().Be(LogLevel.Information);
        entry.Message.Should().Contain("needs-restart");
    }

    [Fact]
    public async Task Progress_logs_Information_with_message()
    {
        var (handler, log) = Build();

        await handler.PublishAsync(
            new ReportingEvent.Progress("hello there") { Origin = "module:FooModule" },
            CancellationToken.None);

        var entry = log.Entries.Should().ContainSingle().Subject;
        entry.Level.Should().Be(LogLevel.Information);
        entry.Message.Should().Contain("hello there");
    }

    [Fact]
    public async Task ProvisioningCompleted_logs_Information()
    {
        var (handler, log) = Build();

        await handler.PublishAsync(
            new ReportingEvent.ProvisioningCompleted { Origin = "stage-runner" },
            CancellationToken.None);

        log.Entries.Should().ContainSingle().Which.Level.Should().Be(LogLevel.Information);
    }

    [Fact]
    public async Task ProvisioningFailed_logs_Error_with_exception()
    {
        var (handler, log) = Build();
        var ex = new InvalidOperationException("boom");

        await handler.PublishAsync(
            new ReportingEvent.ProvisioningFailed("crashed", ex) { Origin = "stage-runner" },
            CancellationToken.None);

        var entry = log.Entries.Should().ContainSingle().Subject;
        entry.Level.Should().Be(LogLevel.Error);
        entry.Exception.Should().BeSameAs(ex);
        entry.Message.Should().Contain("crashed");
    }

    private static (LogReportingHandler handler, CapturingLogger<LogReportingHandler> log) Build()
    {
        var log = new CapturingLogger<LogReportingHandler>();
        return (new LogReportingHandler(log), log);
    }
}
