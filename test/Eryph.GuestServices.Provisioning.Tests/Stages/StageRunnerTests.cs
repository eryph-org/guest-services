using AwesomeAssertions;
using Eryph.GuestServices.Provisioning.DataSources;
using Eryph.GuestServices.Provisioning.Modules;
using Eryph.GuestServices.Provisioning.Reporting;
using Eryph.GuestServices.Provisioning.Reporting.Events;
using Eryph.GuestServices.Provisioning.Semaphores;
using Eryph.GuestServices.Provisioning.Stages;
using Eryph.GuestServices.Provisioning.State;
using Eryph.GuestServices.Provisioning.Tests.Semaphores;
using Eryph.GuestServices.Provisioning.UserData;
using Eryph.GuestServices.Provisioning.Windows;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using CloudConfigModel = global::Eryph.GuestServices.CloudConfig.CloudConfig;

namespace Eryph.GuestServices.Provisioning.Tests.Stages;

public sealed class StageRunnerTests
{
    [Fact]
    public async Task RunAsync_returns_NoDataSource_when_locator_returns_null()
    {
        var locator = Substitute.For<IDataSourceLocator>();
        locator.LocateAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<DataSourceResult?>(null));

        var runner = BuildRunner(locator: locator);
        var result = await runner.RunAsync(CancellationToken.None);

        result.Should().BeOfType<StageRunOutcome.NoDataSource>();
    }

    [Fact]
    public async Task RunAsync_runs_modules_in_stage_order_and_reports_completed()
    {
        var data = MakeData("i-1");
        var reporter = Substitute.For<IReportingDispatcher>();
        var stateStore = new InMemoryStateStore();

        var trace = new List<string>();
        var modules = new IModule[]
        {
            new NetworkRecordingModule(trace),
            new ConfigRecordingAModule(trace),
            new ConfigRecordingBModule(trace),
        };

        var runner = BuildRunner(
            locator: LocatorReturning(data),
            stateStore: stateStore,
            modules: modules,
            reporter: reporter);

        var result = await runner.RunAsync(CancellationToken.None);

        result.Should().BeOfType<StageRunOutcome.Success>();
        trace.Should().Equal("network", "config-a", "config-b");

        await reporter.Received().EmitAsync(
            Arg.Is<ReportingEvent.ProvisioningStarted>(e => e.InstanceId == "i-1"),
            Arg.Any<CancellationToken>());
        await reporter.Received().EmitAsync(
            Arg.Any<ReportingEvent.ProvisioningCompleted>(),
            Arg.Any<CancellationToken>());

        stateStore.Current!.CompletedStages.Should().Contain("Local");
        stateStore.Current.CompletedStages.Should().Contain("Network");
        stateStore.Current.CompletedStages.Should().Contain("Config");
        stateStore.Current.CompletedStages.Should().Contain("Final");
        stateStore.Current.CompletedHandlers.Should().HaveCount(3);
    }

    [Fact]
    public async Task RunAsync_skips_modules_already_completed()
    {
        var data = MakeData("i-1");
        var stateStore = new InMemoryStateStore
        {
            Current = new ProvisioningState
            {
                InstanceId = "i-1",
                CompletedHandlers = [typeof(NetworkRecordingModule).FullName!],
            },
        };

        var trace = new List<string>();
        var modules = new IModule[]
        {
            new NetworkRecordingModule(trace),
            new ConfigRecordingAModule(trace),
        };

        var runner = BuildRunner(LocatorReturning(data), stateStore: stateStore, modules: modules);
        await runner.RunAsync(CancellationToken.None);

        trace.Should().Equal("config-a");
    }

    [Fact]
    public async Task RunAsync_completed_instance_resumes_without_running_modules_and_still_reports_completed()
    {
        var data = MakeData("i-1");
        var reporter = Substitute.For<IReportingDispatcher>();

        var trace = new List<string>();
        var modules = new IModule[]
        {
            new NetworkRecordingModule(trace),
            new ConfigRecordingAModule(trace),
            new ConfigRecordingBModule(trace),
        };

        var allStageNames = Enum.GetNames(typeof(Stage));
        var allModuleKeys = modules.Select(m => m.GetType().FullName!);

        var stateStore = new InMemoryStateStore
        {
            Current = new ProvisioningState
            {
                InstanceId = "i-1",
                CompletedStages = new HashSet<string>(allStageNames, StringComparer.Ordinal),
                CompletedHandlers = new HashSet<string>(allModuleKeys, StringComparer.Ordinal),
            },
        };

        var runner = BuildRunner(LocatorReturning(data), stateStore: stateStore, modules: modules, reporter: reporter);
        var result = await runner.RunAsync(CancellationToken.None);

        result.Should().BeOfType<StageRunOutcome.Success>();
        trace.Should().BeEmpty("all modules were already completed in a prior run");
        await reporter.Received().EmitAsync(
            Arg.Any<ReportingEvent.ProvisioningCompleted>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_resets_state_when_instance_id_changes()
    {
        var data = MakeData("new-instance");
        var stateStore = new InMemoryStateStore
        {
            Current = new ProvisioningState
            {
                InstanceId = "old-instance",
                CompletedHandlers = [typeof(NetworkRecordingModule).FullName!],
            },
        };

        var trace = new List<string>();
        var modules = new IModule[] { new NetworkRecordingModule(trace) };

        var runner = BuildRunner(LocatorReturning(data), stateStore: stateStore, modules: modules);
        await runner.RunAsync(CancellationToken.None);

        trace.Should().Equal("network");
        stateStore.Resets.Should().Be(1);
        stateStore.Current!.InstanceId.Should().Be("new-instance");
    }

    [Fact]
    public async Task RunAsync_returns_RebootRequested_and_records_module_completed()
    {
        var data = MakeData("i-1");
        var stateStore = new InMemoryStateStore();
        var reporter = Substitute.For<IReportingDispatcher>();

        var trace = new List<string>();
        var modules = new IModule[]
        {
            new RebootingModule("needs-reboot"),
            new ConfigRecordingAModule(trace),
        };

        var runner = BuildRunner(LocatorReturning(data), stateStore: stateStore, modules: modules, reporter: reporter);
        var result = await runner.RunAsync(CancellationToken.None);

        result.Should().BeOfType<StageRunOutcome.RebootRequested>()
            .Which.Reason.Should().Be("needs-reboot");
        trace.Should().BeEmpty();
        // docs/bugs/0001: a reboot-pending module must NOT appear in
        // CompletedHandlers — that's what made the bug silent.
        stateStore.Current!.PendingHandlers.Should().Contain(typeof(RebootingModule).FullName!);
        stateStore.Current.CompletedHandlers.Should().NotContain(typeof(RebootingModule).FullName!);
        stateStore.Current.RebootCount.Should().Be(1);
        await reporter.Received().EmitAsync(
            Arg.Is<ReportingEvent.RebootRequested>(e => e.Reason == "needs-reboot"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_returns_Failed_when_module_returns_failed()
    {
        var data = MakeData("i-1");
        var reporter = Substitute.For<IReportingDispatcher>();
        var stateStore = new InMemoryStateStore();

        var modules = new IModule[] { new FailingModule("boom") };

        var runner = BuildRunner(LocatorReturning(data), stateStore: stateStore, modules: modules, reporter: reporter);
        var result = await runner.RunAsync(CancellationToken.None);

        result.Should().BeOfType<StageRunOutcome.Failed>().Which.Reason.Should().Be("boom");
        await reporter.Received().EmitAsync(
            Arg.Is<ReportingEvent.ProvisioningFailed>(e => e.Reason.Contains("boom")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_returns_Failed_when_module_throws()
    {
        var data = MakeData("i-1");
        var reporter = Substitute.For<IReportingDispatcher>();
        var modules = new IModule[] { new ThrowingModule() };

        var runner = BuildRunner(LocatorReturning(data), modules: modules, reporter: reporter);
        var result = await runner.RunAsync(CancellationToken.None);

        result.Should().BeOfType<StageRunOutcome.Failed>();
        await reporter.Received().EmitAsync(
            Arg.Any<ReportingEvent.ProvisioningFailed>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunStageAsync_runs_only_the_requested_stage()
    {
        var data = MakeData("i-1");
        var stateStore = new InMemoryStateStore();

        var trace = new List<string>();
        var modules = new IModule[]
        {
            new NetworkRecordingModule(trace),
            new ConfigRecordingAModule(trace),
            new ConfigRecordingBModule(trace),
        };

        var runner = BuildRunner(LocatorReturning(data), stateStore: stateStore, modules: modules);
        var result = await runner.RunStageAsync(Stage.Config, CancellationToken.None);

        result.Should().BeOfType<StageRunOutcome.Success>();
        trace.Should().Equal("config-a", "config-b");
        // Only the requested stage gets recorded as completed.
        stateStore.Current!.CompletedStages.Should().BeEquivalentTo(["Config"]);
    }

    [Fact]
    public async Task RunStageAsync_for_Local_does_not_require_userdata_resolution()
    {
        var data = MakeData("i-1");
        var pipeline = Substitute.For<IUserDataPipeline>();
        // No setup — calling pipeline.ResolveAsync would trigger NSubstitute defaults.

        var runner = BuildRunner(LocatorReturning(data), pipeline: pipeline);
        var result = await runner.RunStageAsync(Stage.Local, CancellationToken.None);

        result.Should().BeOfType<StageRunOutcome.Success>();
        await pipeline.DidNotReceiveWithAnyArgs().ResolveAsync(default, default);
    }

    // ---- RFC 0005 cleanup hook ----

    [Fact]
    public async Task RunAsync_invokes_data_source_cleanup_only_on_full_Success()
    {
        // Per RFC 0005: OnProvisioningCompletedAsync must be invoked when the
        // Final stage completes successfully. The locator routes the call to
        // the originating datasource. The runner only owns the dispatch — we
        // verify it happens exactly once and the success path is not impacted.
        var data = MakeData("i-cleanup");
        var locator = LocatorReturning(data);

        var runner = BuildRunner(locator);
        var result = await runner.RunAsync(CancellationToken.None);

        result.Should().BeOfType<StageRunOutcome.Success>();
        await locator.Received(1).OnProvisioningCompletedAsync(data, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_does_not_invoke_cleanup_on_RebootRequested()
    {
        // Reboot-and-continue must keep the datasource alive across the boot —
        // the second-half pass needs to read the same payload. Cleanup is for
        // *terminal* success only.
        var data = MakeData("i-1");
        var locator = LocatorReturning(data);

        var modules = new IModule[] { new RebootingModule("needs-reboot") };

        var runner = BuildRunner(locator, modules: modules);
        var result = await runner.RunAsync(CancellationToken.None);

        result.Should().BeOfType<StageRunOutcome.RebootRequested>();
        await locator.DidNotReceive().OnProvisioningCompletedAsync(
            Arg.Any<DataSourceResult>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_does_not_invoke_cleanup_on_Failed()
    {
        // A failed module returns Failed; the datasource may still be needed
        // for diagnostics / re-run. Cleanup must NOT fire.
        var data = MakeData("i-1");
        var locator = LocatorReturning(data);

        var modules = new IModule[] { new FailingModule("boom") };

        var runner = BuildRunner(locator, modules: modules);
        var result = await runner.RunAsync(CancellationToken.None);

        result.Should().BeOfType<StageRunOutcome.Failed>();
        await locator.DidNotReceive().OnProvisioningCompletedAsync(
            Arg.Any<DataSourceResult>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_keeps_Success_when_cleanup_hook_throws()
    {
        // RFC 0005 explicit requirement: a throwing cleanup hook must not
        // flip provisioning to Failed. We model the locator throwing because
        // a misbehaving datasource forwards exceptions through the locator's
        // try/catch. The StageRunner wraps the call so the outcome stays
        // Success.
        var data = MakeData("i-1");
        var locator = Substitute.For<IDataSourceLocator>();
        locator.LocateAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<DataSourceResult?>(data));
        locator.OnProvisioningCompletedAsync(Arg.Any<DataSourceResult>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("cleanup failed"));

        var runner = BuildRunner(locator);
        var result = await runner.RunAsync(CancellationToken.None);

        result.Should().BeOfType<StageRunOutcome.Success>(
            "cleanup is best-effort; provisioning already succeeded by the time it runs");
    }

    [Fact]
    public async Task RunAsync_returns_Failed_when_userdata_cannot_be_parsed()
    {
        var data = MakeData("i-1", userData: "not valid yaml maybe");
        var pipeline = Substitute.For<IUserDataPipeline>();
        pipeline.ResolveAsync(Arg.Any<byte[]?>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("bad yaml"));
        var reporter = Substitute.For<IReportingDispatcher>();

        var runner = BuildRunner(LocatorReturning(data), pipeline: pipeline, reporter: reporter);
        var result = await runner.RunAsync(CancellationToken.None);

        result.Should().BeOfType<StageRunOutcome.Failed>();
        await reporter.Received().EmitAsync(
            Arg.Is<ReportingEvent.ProvisioningFailed>(e => e.Reason.StartsWith("userdata-parse")),
            Arg.Any<CancellationToken>());
    }

    private static IDataSourceLocator LocatorReturning(DataSourceResult result)
    {
        var locator = Substitute.For<IDataSourceLocator>();
        locator.LocateAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<DataSourceResult?>(result));
        return locator;
    }

    private static DataSourceResult MakeData(string instanceId, string? userData = null) => new()
    {
        SourceName = "test",
        InstanceId = instanceId,
        UserData = userData is null ? null : System.Text.Encoding.UTF8.GetBytes(userData),
    };

    private static StageRunner BuildRunner(
        IDataSourceLocator? locator = null,
        IUserDataPipeline? pipeline = null,
        IStateStore? stateStore = null,
        IModule[]? modules = null,
        IReportingDispatcher? reporter = null,
        ISemaphoreStore? semaphoreStore = null,
        IBootSessionDetector? bootSessionDetector = null)
    {
        locator ??= Substitute.For<IDataSourceLocator>();
        if (pipeline is null)
        {
            pipeline = Substitute.For<IUserDataPipeline>();
            pipeline.ResolveAsync(Arg.Any<byte[]?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(ResolvedUserData.Empty(new CloudConfigModel())));
        }
        stateStore ??= new InMemoryStateStore();
        reporter ??= Substitute.For<IReportingDispatcher>();
        semaphoreStore ??= new NullSemaphoreStore();
        if (bootSessionDetector is null)
        {
            bootSessionDetector = Substitute.For<IBootSessionDetector>();
            bootSessionDetector.IsNewBootAsync(Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(false));
        }

        return new StageRunner(
            locator,
            pipeline,
            stateStore,
            semaphoreStore,
            bootSessionDetector,
            modules ?? [],
            reporter,
            Substitute.For<IWindowsOs>(),
            new Eryph.GuestServices.Provisioning.Configuration.ProvisioningSettings(),
            NullLogger<StageRunner>.Instance);
    }

    private sealed class InMemoryStateStore : IStateStore
    {
        public ProvisioningState? Current { get; set; }
        public int Resets { get; private set; }

        public Task<ProvisioningState?> LoadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Current);

        public Task SaveAsync(ProvisioningState state, CancellationToken cancellationToken)
        {
            Current = state;
            return Task.CompletedTask;
        }

        public Task ResetAsync(CancellationToken cancellationToken)
        {
            Current = null;
            Resets++;
            return Task.CompletedTask;
        }
    }

    [Stage(Stage.Network)]
    internal sealed class NetworkRecordingModule(List<string> trace) : IModule
    {
        public Task<ModuleOutcome> ApplyAsync(ResolvedUserData userData, IModuleContext context, CancellationToken cancellationToken)
        {
            trace.Add("network");
            return Task.FromResult(ModuleOutcome.Ok());
        }
    }

    [Stage(Stage.Config, Order = 0)]
    internal sealed class ConfigRecordingAModule(List<string> trace) : IModule
    {
        public Task<ModuleOutcome> ApplyAsync(ResolvedUserData userData, IModuleContext context, CancellationToken cancellationToken)
        {
            trace.Add("config-a");
            return Task.FromResult(ModuleOutcome.Ok());
        }
    }

    [Stage(Stage.Config, Order = 1)]
    internal sealed class ConfigRecordingBModule(List<string> trace) : IModule
    {
        public Task<ModuleOutcome> ApplyAsync(ResolvedUserData userData, IModuleContext context, CancellationToken cancellationToken)
        {
            trace.Add("config-b");
            return Task.FromResult(ModuleOutcome.Ok());
        }
    }

    [Stage(Stage.Network)]
    private sealed class RebootingModule(string reason) : IModule
    {
        public Task<ModuleOutcome> ApplyAsync(ResolvedUserData userData, IModuleContext context, CancellationToken cancellationToken) =>
            Task.FromResult(ModuleOutcome.Reboot(reason));
    }

    [Stage(Stage.Network)]
    private sealed class FailingModule(string reason) : IModule
    {
        public Task<ModuleOutcome> ApplyAsync(ResolvedUserData userData, IModuleContext context, CancellationToken cancellationToken) =>
            Task.FromResult(ModuleOutcome.Fail(reason));
    }

    [Stage(Stage.Network)]
    private sealed class ThrowingModule : IModule
    {
        public Task<ModuleOutcome> ApplyAsync(ResolvedUserData userData, IModuleContext context, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("boom");
    }
}
