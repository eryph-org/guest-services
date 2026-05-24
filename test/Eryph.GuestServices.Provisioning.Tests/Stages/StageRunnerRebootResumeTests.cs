using AwesomeAssertions;
using Eryph.GuestServices.Provisioning.Configuration;
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
using CloudConfigModel = global::Eryph.GuestServices.CloudConfig.CloudConfig;

namespace Eryph.GuestServices.Provisioning.Tests.Stages;

/// <summary>
/// Regression tests for docs/bugs/0001-scriptsusermodule-skips-queue-after-reboot.md.
///
/// Pre-fix the StageRunner only checked semaphore existence, not its
/// outcome value. Once any module wrote a `reboot-requested` marker the
/// post-reboot pass skipped that module entirely — so any work declared
/// after a 1003-returning script never ran.
///
/// The contract these tests pin down:
/// 1. A module that returns Reboot can be re-entered after the reboot.
/// 2. On re-entry the module's second outcome ("completed") replaces the
///    "reboot-requested" marker; the dispatcher then proceeds to later
///    modules in the same stage and to subsequent stages.
/// 3. state.json reflects PendingHandlers vs CompletedHandlers so external
///    consumers (eryph-genes Pester) can tell "all done" from "stuck on a
///    reboot that didn't happen".
/// 4. A module that requests reboot more than the per-module cap is treated
///    as a hard failure; the runner does not loop forever.
/// </summary>
public sealed class StageRunnerRebootResumeTests
{
    [Fact]
    public async Task Module_that_returns_Reboot_is_re_entered_on_next_run()
    {
        // Arrange: a module that returns Reboot the first time, Completed the second.
        var data = MakeData("i-1");
        var stateStore = new InMemoryStateStore();
        var semaphoreStore = new InMemorySemaphoreStore();
        var module = new RebootOnceThenCompleteModule("needs-reboot");

        var runner = BuildRunner(
            locator: LocatorReturning(data),
            stateStore: stateStore,
            semaphoreStore: semaphoreStore,
            modules: [module]);

        // Act 1: first run → reboot requested.
        var first = await runner.RunAsync(CancellationToken.None);

        // Act 2: simulate post-reboot resume.
        var second = await runner.RunAsync(CancellationToken.None);

        // Assert: BUG today — the module is skipped on the second run because
        // the semaphore "exists" regardless of outcome value. With the fix,
        // the module is re-entered, returns Completed, and the run succeeds.
        first.Should().BeOfType<StageRunOutcome.RebootRequested>();
        second.Should().BeOfType<StageRunOutcome.Success>();
        module.CallCount.Should().Be(2, "the module must be re-entered after its reboot request");
    }

    [Fact]
    public async Task State_json_distinguishes_PendingHandlers_from_CompletedHandlers()
    {
        var data = MakeData("i-1");
        var stateStore = new InMemoryStateStore();
        var semaphoreStore = new InMemorySemaphoreStore();
        var module = new RebootOnceThenCompleteModule("needs-reboot");

        var runner = BuildRunner(
            locator: LocatorReturning(data),
            stateStore: stateStore,
            semaphoreStore: semaphoreStore,
            modules: [module]);

        // After the first run, the module is waiting for reboot resume.
        await runner.RunAsync(CancellationToken.None);

        stateStore.Current!.PendingHandlers
            .Should().Contain(typeof(RebootOnceThenCompleteModule).FullName!,
                "a reboot-pending handler must NOT be advertised as completed");
        stateStore.Current.CompletedHandlers
            .Should().NotContain(typeof(RebootOnceThenCompleteModule).FullName!,
                "completed reporting in state.json must not lie about reboot-pending work");

        // After the second run, the same handler is fully completed and no
        // longer pending.
        await runner.RunAsync(CancellationToken.None);

        stateStore.Current.PendingHandlers.Should().BeEmpty();
        stateStore.Current.CompletedHandlers
            .Should().Contain(typeof(RebootOnceThenCompleteModule).FullName!);
    }

    [Fact]
    public async Task Modules_declared_after_a_reboot_requesting_module_run_after_resume()
    {
        // Two modules in the same stage. First returns Reboot once, then
        // Completed. Second is a plain recorder. Pre-fix the recorder is
        // never reached because the StageRunner exits the stage on first
        // Reboot and skips both modules on resume.
        var data = MakeData("i-1");
        var stateStore = new InMemoryStateStore();
        var semaphoreStore = new InMemorySemaphoreStore();

        var trace = new List<string>();
        var rebooter = new RebootOnceThenCompleteModule("needs-reboot");
        var recorder = new RecordingModule(trace, "after-reboot");

        var runner = BuildRunner(
            locator: LocatorReturning(data),
            stateStore: stateStore,
            semaphoreStore: semaphoreStore,
            modules: [rebooter, recorder]);

        // First run: reboot requested by `rebooter`; `recorder` not called yet.
        var first = await runner.RunAsync(CancellationToken.None);
        first.Should().BeOfType<StageRunOutcome.RebootRequested>();
        trace.Should().BeEmpty();

        // Resume.
        var second = await runner.RunAsync(CancellationToken.None);

        second.Should().BeOfType<StageRunOutcome.Success>();
        trace.Should().Equal("after-reboot");
        rebooter.CallCount.Should().Be(2);
    }

    [Fact]
    public async Task Per_module_reboot_cap_fails_the_run_instead_of_looping_forever()
    {
        // A misbehaving module returns Reboot forever. Without a cap the
        // StageRunner would loop on every boot. The cap converts the
        // pathological case into a Failed outcome.
        var data = MakeData("i-1");
        var stateStore = new InMemoryStateStore();
        var semaphoreStore = new InMemorySemaphoreStore();
        var module = new AlwaysRebootModule("always-reboots");

        var runner = BuildRunner(
            locator: LocatorReturning(data),
            stateStore: stateStore,
            semaphoreStore: semaphoreStore,
            modules: [module]);

        // The cap is 3 reboots per module. Call until we either fail or
        // hit a sanity bound.
        StageRunOutcome? final = null;
        for (var i = 0; i < 10; i++)
        {
            final = await runner.RunAsync(CancellationToken.None);
            if (final is StageRunOutcome.Failed) break;
        }

        final.Should().BeOfType<StageRunOutcome.Failed>(
            "a module that keeps requesting reboot without progress must be failed, not looped forever");
        module.CallCount.Should().BeLessThan(10, "the cap prevents unbounded re-entry");
    }

    [Fact]
    public async Task Per_module_reboot_cap_honors_custom_MaxPerModule_setting()
    {
        // A tighter cap of 1 must fail after a single reboot, proving the cap
        // reads from ProvisioningSettings.Reboot.MaxPerModule.
        var data = MakeData("i-1");
        var stateStore = new InMemoryStateStore();
        var semaphoreStore = new InMemorySemaphoreStore();
        var module = new AlwaysRebootModule("always-reboots");

        var runner = BuildRunner(
            locator: LocatorReturning(data),
            stateStore: stateStore,
            semaphoreStore: semaphoreStore,
            modules: [module],
            settings: new ProvisioningSettings { Reboot = new RebootSettings { MaxPerModule = 1 } });

        StageRunOutcome? final = null;
        for (var i = 0; i < 10; i++)
        {
            final = await runner.RunAsync(CancellationToken.None);
            if (final is StageRunOutcome.Failed) break;
        }

        final.Should().BeOfType<StageRunOutcome.Failed>();
        // Default cap (3) would call the module 4 times; cap=1 must trip sooner.
        module.CallCount.Should().BeLessThanOrEqualTo(2, "MaxPerModule=1 fails after one reboot");
    }

    // ----- helpers -----

    private static IDataSourceLocator LocatorReturning(DataSourceResult result)
    {
        var locator = Substitute.For<IDataSourceLocator>();
        locator.LocateAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<DataSourceResult?>(result));
        return locator;
    }

    private static DataSourceResult MakeData(string instanceId) => new()
    {
        SourceName = "test",
        InstanceId = instanceId,
        UserData = null,
    };

    private static StageRunner BuildRunner(
        IDataSourceLocator locator,
        IStateStore stateStore,
        ISemaphoreStore semaphoreStore,
        IModule[] modules,
        ProvisioningSettings? settings = null)
    {
        var pipeline = Substitute.For<IUserDataPipeline>();
        pipeline.ResolveAsync(Arg.Any<byte[]?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(ResolvedUserData.Empty(new CloudConfigModel())));

        var bootDetector = Substitute.For<IBootSessionDetector>();
        bootDetector.IsNewBootAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(false));

        return new StageRunner(
            locator,
            pipeline,
            stateStore,
            semaphoreStore,
            bootDetector,
            modules,
            Substitute.For<IReportingDispatcher>(),
            Substitute.For<IWindowsOs>(),
            settings ?? new ProvisioningSettings(),
            NullLogger<StageRunner>.Instance);
    }

    // In-memory implementations that mirror FileSemaphoreStore/FileStateStore
    // semantics but live entirely in process memory.
    internal sealed class InMemoryStateStore : IStateStore
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

    internal sealed class InMemorySemaphoreStore : ISemaphoreStore
    {
        private readonly Dictionary<string, string> _markers = new(StringComparer.Ordinal);

        public Task<bool> ExistsAsync(string moduleKey, ModuleFrequency frequency, string instanceId, CancellationToken cancellationToken) =>
            Task.FromResult(_markers.ContainsKey(Key(moduleKey, frequency, instanceId)));

        public Task<string?> ReadOutcomeAsync(string moduleKey, ModuleFrequency frequency, string instanceId, CancellationToken cancellationToken)
        {
            _markers.TryGetValue(Key(moduleKey, frequency, instanceId), out var v);
            return Task.FromResult(v);
        }

        public Task WriteAsync(string moduleKey, ModuleFrequency frequency, string instanceId, string outcome, CancellationToken cancellationToken)
        {
            _markers[Key(moduleKey, frequency, instanceId)] = outcome;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> ListPerInstanceAsync(string instanceId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>(_markers.Keys
                .Where(k => k.StartsWith($"{ModuleFrequency.PerInstance}:{instanceId}:", StringComparison.Ordinal))
                .ToList());

        public Task ClearPerInstanceAsync(string instanceId, CancellationToken cancellationToken)
        {
            foreach (var k in _markers.Keys.Where(k => k.Contains(instanceId, StringComparison.Ordinal)).ToList())
                _markers.Remove(k);
            return Task.CompletedTask;
        }

        public Task ClearPerBootAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ClearPerOnceAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        private static string Key(string moduleKey, ModuleFrequency frequency, string instanceId) =>
            frequency == ModuleFrequency.PerInstance
                ? $"{frequency}:{instanceId}:{moduleKey}"
                : $"{frequency}::{moduleKey}";
    }

    [Stage(Stage.Final, Order = 0)]
    private sealed class RebootOnceThenCompleteModule(string reason) : IModule
    {
        public int CallCount { get; private set; }

        public Task<ModuleOutcome> ApplyAsync(ResolvedUserData userData, IModuleContext context, CancellationToken cancellationToken)
        {
            CallCount++;
            return CallCount == 1
                ? Task.FromResult<ModuleOutcome>(ModuleOutcome.Reboot(reason))
                : Task.FromResult(ModuleOutcome.Ok());
        }
    }

    [Stage(Stage.Final, Order = 1)]
    private sealed class RecordingModule(List<string> trace, string label) : IModule
    {
        public Task<ModuleOutcome> ApplyAsync(ResolvedUserData userData, IModuleContext context, CancellationToken cancellationToken)
        {
            trace.Add(label);
            return Task.FromResult(ModuleOutcome.Ok());
        }
    }

    [Stage(Stage.Final, Order = 0)]
    private sealed class AlwaysRebootModule(string reason) : IModule
    {
        public int CallCount { get; private set; }

        public Task<ModuleOutcome> ApplyAsync(ResolvedUserData userData, IModuleContext context, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult<ModuleOutcome>(ModuleOutcome.Reboot(reason));
        }
    }
}
