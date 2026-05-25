using AwesomeAssertions;
using Eryph.GuestServices.Provisioning.DataSources;
using Eryph.GuestServices.Provisioning.Modules;
using Eryph.GuestServices.Provisioning.Reporting;
using Eryph.GuestServices.Provisioning.Semaphores;
using Eryph.GuestServices.Provisioning.Stages;
using Eryph.GuestServices.Provisioning.State;
using Eryph.GuestServices.Provisioning.Tests.Semaphores;
using Eryph.GuestServices.Provisioning.UserData;
using Eryph.GuestServices.Provisioning.Windows;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using CloudConfigModel = global::Eryph.GuestServices.CloudConfig.CloudConfig;

namespace Eryph.GuestServices.Provisioning.Tests.Stages;

/// <summary>
/// Tests for RFC 0003 / 0010: per-instance / per-boot / per-once module
/// frequencies, gated through <see cref="ISemaphoreStore"/>.
/// </summary>
public sealed class StageRunnerFrequencyTests : IDisposable
{
    private readonly string _tempRoot;

    public StageRunnerFrequencyTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "egs-frequency-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }

    // Regression: the production debug session hit a re-run idempotency issue
    // (Admin user password policy) that was partly a frequency-shaped problem.
    // A per-instance module must NOT re-run for the same instance-id, even if
    // the agent is invoked twice. The only previous way to force a re-run was
    // to delete state.json wholesale.
    [Fact]
    public async Task PerInstance_module_runs_once_then_skips_on_second_run_for_same_instance()
    {
        var trace = new List<string>();
        var modules = new IModule[] { new RecordingPerInstanceModule(trace) };
        var (locator, semaphoreStore) = BuildSharedRuntime("i-1");

        var runner1 = NewRunner(locator, semaphoreStore, modules);
        await runner1.RunAsync(CancellationToken.None);
        trace.Should().Equal("recorded");

        // Second invocation simulates the operator running provisioning again
        // (e.g. after an agent restart) without touching state.json.
        var runner2 = NewRunner(locator, semaphoreStore, modules);
        await runner2.RunAsync(CancellationToken.None);

        trace.Should().Equal(new[] { "recorded" }, "the per-instance module must not re-run on the same instance-id");
    }

    [Fact]
    public async Task PerBoot_module_runs_again_after_new_boot_is_detected()
    {
        var trace = new List<string>();
        var modules = new IModule[] { new RecordingPerBootModule(trace) };
        var (locator, semaphoreStore) = BuildSharedRuntime("i-1");
        var bootDetector = new StubBootSessionDetector { NextResult = true };

        var runner = NewRunner(locator, semaphoreStore, modules, bootDetector);
        await runner.RunAsync(CancellationToken.None);
        trace.Should().Equal("per-boot");

        // Same boot — module must not re-run.
        bootDetector.NextResult = false;
        await runner.RunAsync(CancellationToken.None);
        trace.Should().Equal("per-boot");

        // Simulate the next boot.
        bootDetector.NextResult = true;
        await runner.RunAsync(CancellationToken.None);
        trace.Should().Equal("per-boot", "per-boot");
    }

    [Fact]
    public async Task PerOnce_module_never_runs_more_than_once_even_across_instance_id_change()
    {
        var trace = new List<string>();
        var modules = new IModule[] { new RecordingPerOnceModule(trace) };
        var (locator, semaphoreStore) = BuildSharedRuntime("i-1");

        await NewRunner(locator, semaphoreStore, modules).RunAsync(CancellationToken.None);
        trace.Should().Equal("per-once");

        // Re-deploy: a new instance-id is observed. PerInstance markers get
        // cleared but per-once survives.
        var (locator2, _) = BuildSharedRuntime("i-2");
        await NewRunner(locator2, semaphoreStore, modules).RunAsync(CancellationToken.None);

        trace.Should().Equal(new[] { "per-once" }, "per-once survives an instance-id change");
    }

    // The reboot-and-continue contract: a module that returns Reboot must have
    // its semaphore written BEFORE the runner returns. Otherwise the
    // post-reboot pass would re-execute it and we would loop forever.
    [Fact]
    public async Task RebootRequesting_PerInstance_module_writes_semaphore_before_returning()
    {
        var modules = new IModule[] { new RebootingPerInstanceModule() };
        var (locator, semaphoreStore) = BuildSharedRuntime("i-1");

        var outcome = await NewRunner(locator, semaphoreStore, modules).RunAsync(CancellationToken.None);

        outcome.Should().BeOfType<StageRunOutcome.RebootRequested>();
        (await semaphoreStore.ExistsAsync(
            typeof(RebootingPerInstanceModule).FullName!,
            ModuleFrequency.PerInstance,
            "i-1",
            CancellationToken.None))
            .Should().BeTrue();
    }

    [Fact]
    public async Task Failing_module_does_NOT_write_semaphore()
    {
        var modules = new IModule[] { new FailingPerInstanceModule() };
        var (locator, semaphoreStore) = BuildSharedRuntime("i-1");

        await NewRunner(locator, semaphoreStore, modules).RunAsync(CancellationToken.None);

        (await semaphoreStore.ExistsAsync(
            typeof(FailingPerInstanceModule).FullName!,
            ModuleFrequency.PerInstance,
            "i-1",
            CancellationToken.None))
            .Should().BeFalse("a failed run must re-execute on the next pass");
    }

    // Migration path: an existing state.json with CompletedHandlers must
    // promote those entries into the per-instance semaphore directory and
    // skip the corresponding modules on next run.
    [Fact]
    public async Task LegacyCompletedHandlers_AreMigratedToPerInstanceSemaphores()
    {
        var trace = new List<string>();
        var modules = new IModule[] { new RecordingPerInstanceModule(trace) };

        var legacyState = new ProvisioningState
        {
            InstanceId = "i-legacy",
            CompletedHandlers = [typeof(RecordingPerInstanceModule).FullName!],
        };
        var stateStore = new InMemoryStateStore { Current = legacyState };
        var (locator, semaphoreStore) = BuildSharedRuntime("i-legacy");

        await NewRunner(locator, semaphoreStore, modules, stateStore: stateStore)
            .RunAsync(CancellationToken.None);

        trace.Should().BeEmpty("the legacy CompletedHandlers entry should have been migrated to a semaphore and skip the module");
        (await semaphoreStore.ExistsAsync(
            typeof(RecordingPerInstanceModule).FullName!,
            ModuleFrequency.PerInstance,
            "i-legacy",
            CancellationToken.None))
            .Should().BeTrue();
    }

    private (IDataSourceLocator Locator, FileSemaphoreStore SemaphoreStore) BuildSharedRuntime(string instanceId)
    {
        var locator = Substitute.For<IDataSourceLocator>();
        locator.LocateAsync(Arg.Any<CancellationToken>()).Returns(
            new DataSourceResult { SourceName = "test", InstanceId = instanceId });
        var sem = new FileSemaphoreStore(NullLogger<FileSemaphoreStore>.Instance, _tempRoot);
        return (locator, sem);
    }

    private static StageRunner NewRunner(
        IDataSourceLocator locator,
        ISemaphoreStore semaphoreStore,
        IModule[] modules,
        IBootSessionDetector? bootDetector = null,
        IStateStore? stateStore = null)
    {
        var pipeline = Substitute.For<IUserDataPipeline>();
        pipeline.ResolveAsync(Arg.Any<byte[]?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(ResolvedUserData.Empty(new CloudConfigModel())));
        return new StageRunner(
            locator,
            pipeline,
            stateStore ?? new InMemoryStateStore(),
            semaphoreStore,
            bootDetector ?? new StubBootSessionDetector { NextResult = false },
            modules,
            Substitute.For<IReportingDispatcher>(),
            Substitute.For<IWindowsOs>(),
            new Eryph.GuestServices.Provisioning.Configuration.ProvisioningSettings(),
            NullLogger<StageRunner>.Instance);
    }

    private sealed class InMemoryStateStore : IStateStore
    {
        public ProvisioningState? Current { get; set; }

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
            return Task.CompletedTask;
        }
    }

    [Stage(Stage.Config, Frequency = ModuleFrequency.PerInstance)]
    private sealed class RecordingPerInstanceModule(List<string> trace) : IModule
    {
        public Task<ModuleOutcome> ApplyAsync(ResolvedUserData userData, IModuleContext context, CancellationToken cancellationToken)
        {
            trace.Add("recorded");
            return Task.FromResult(ModuleOutcome.Ok());
        }
    }

    [Stage(Stage.Config, Frequency = ModuleFrequency.PerBoot)]
    private sealed class RecordingPerBootModule(List<string> trace) : IModule
    {
        public Task<ModuleOutcome> ApplyAsync(ResolvedUserData userData, IModuleContext context, CancellationToken cancellationToken)
        {
            trace.Add("per-boot");
            return Task.FromResult(ModuleOutcome.Ok());
        }
    }

    [Stage(Stage.Config, Frequency = ModuleFrequency.PerOnce)]
    private sealed class RecordingPerOnceModule(List<string> trace) : IModule
    {
        public Task<ModuleOutcome> ApplyAsync(ResolvedUserData userData, IModuleContext context, CancellationToken cancellationToken)
        {
            trace.Add("per-once");
            return Task.FromResult(ModuleOutcome.Ok());
        }
    }

    [Stage(Stage.Config, Frequency = ModuleFrequency.PerInstance)]
    private sealed class RebootingPerInstanceModule : IModule
    {
        public Task<ModuleOutcome> ApplyAsync(ResolvedUserData userData, IModuleContext context, CancellationToken cancellationToken) =>
            Task.FromResult(ModuleOutcome.Reboot("test"));
    }

    [Stage(Stage.Config, Frequency = ModuleFrequency.PerInstance)]
    private sealed class FailingPerInstanceModule : IModule
    {
        public Task<ModuleOutcome> ApplyAsync(ResolvedUserData userData, IModuleContext context, CancellationToken cancellationToken) =>
            Task.FromResult(ModuleOutcome.Fail("nope"));
    }
}
