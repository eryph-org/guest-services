using Eryph.GuestServices.Core;
using Eryph.GuestServices.Provisioning.Hosting;
using Eryph.GuestServices.Provisioning.Reporting;
using Eryph.GuestServices.Provisioning.Reporting.Events;
using Eryph.GuestServices.Provisioning.Stages;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Eryph.GuestServices.Provisioning.Tests.Hosting;

public class ProvisioningHostedServiceGateTests
{
    [Fact]
    public async Task ExecuteAsync_ProvisioningDisabled_DoesNotRunStageRunner()
    {
        var runner = Substitute.For<IStageRunner>();
        var service = CreateService(runner, provisioningEnabled: false);

        await RunOnceAsync(service);

        await runner.DidNotReceiveWithAnyArgs().RunAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_ProvisioningEnabled_RunsStageRunner()
    {
        var runner = Substitute.For<IStageRunner>();
        runner.RunAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<StageRunOutcome>(StageRunOutcome.Success.Instance));
        var service = CreateService(runner, provisioningEnabled: true);

        await RunOnceAsync(service);

        await runner.Received(1).RunAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_StageRunnerThrows_ReportsFailedState()
    {
        // Regression: a state-save replace denied by AV threw out of the stage
        // runner and was swallowed, leaving KVP stuck on `running`. The crash must
        // now surface as a `failed` reporting event so the host reports it.
        var runner = Substitute.For<IStageRunner>();
        runner.RunAsync(Arg.Any<CancellationToken>())
            .Returns<Task<StageRunOutcome>>(_ => throw new UnauthorizedAccessException("denied"));
        var reporter = Substitute.For<IReportingDispatcher>();
        var service = CreateService(runner, provisioningEnabled: true, reporter);

        await RunOnceAsync(service);

        await reporter.Received(1).EmitAsync(
            Arg.Is<ReportingEvent>(e => e is ReportingEvent.ProvisioningFailed),
            Arg.Any<CancellationToken>());
    }

    private static ProvisioningHostedService CreateService(
        IStageRunner runner, bool provisioningEnabled, IReportingDispatcher? reporter = null)
    {
        var lifetime = Substitute.For<IHostApplicationLifetime>();
        var flags = new FixedFlags(provisioningEnabled);
        return new ProvisioningHostedService(
            runner, lifetime, flags, reporter ?? Substitute.For<IReportingDispatcher>(),
            NullLogger<ProvisioningHostedService>.Instance);
    }

    // BackgroundService.StartAsync kicks off ExecuteAsync and exposes the task
    // via ExecuteTask. Awaiting it directly is deterministic: both the disabled
    // short-circuit and the substitute's synchronous Success run to completion
    // (no real I/O), so we don't depend on StopAsync cancellation timing.
    private static async Task RunOnceAsync(ProvisioningHostedService service)
    {
        await service.StartAsync(CancellationToken.None);
        if (service.ExecuteTask is not null)
            await service.ExecuteTask;
    }

    private sealed class FixedFlags(bool provisioningEnabled) : IServiceControlFlags
    {
        public bool IsProvisioningEnabled() => provisioningEnabled;
        public bool IsRemoteAccessEnabled() => true;
    }
}
