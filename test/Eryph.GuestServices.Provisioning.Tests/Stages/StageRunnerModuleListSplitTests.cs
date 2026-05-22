using AwesomeAssertions;
using Eryph.GuestServices.Provisioning.Configuration;
using Eryph.GuestServices.Provisioning.DataSources;
using Eryph.GuestServices.Provisioning.Modules;
using Eryph.GuestServices.Provisioning.Reporting;
using Eryph.GuestServices.Provisioning.Semaphores;
using Eryph.GuestServices.Provisioning.Stages;
using Eryph.GuestServices.Provisioning.State;
using Eryph.GuestServices.Provisioning.UserData;
using Eryph.GuestServices.Provisioning.Windows;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using CloudConfigModel = Eryph.GuestServices.CloudConfig.CloudConfig;

namespace Eryph.GuestServices.Provisioning.Tests.Stages;

/// <summary>
/// RFC 0009: per-stage allowlist / denylist of modules. These tests assert
/// the filter applied by <see cref="StageRunner"/> when
/// <see cref="ProvisioningSettings.Stages"/> is populated:
/// - default (no settings) → all discovered modules in the stage run
/// - EnabledModules set → only listed ones run
/// - DisabledModules set → others skipped
/// - Both set → enable narrows first, disable removes from the narrowed set
/// - Unknown name → logged warning, not a hard failure
/// - Stage key case-insensitive
/// - Match tolerates "Module" suffix
/// </summary>
public sealed class StageRunnerModuleListSplitTests
{
    [Fact]
    public async Task NoStageSettings_AllModulesRun()
    {
        // No EnabledModules / DisabledModules → discovered modules run as-is.
        // (NSubstitute proxies lack the [Stage] attribute, so we use the
        // attribute-bearing fakes declared at the bottom of this file.)
        var moduleA = new FakeAllowedModule();
        var moduleB = new FakeBlockedModule();

        var runner = BuildRunner(new ProvisioningSettings(), moduleA, moduleB);

        await runner.RunAsync(CancellationToken.None);

        moduleA.RanCount.Should().Be(1);
        moduleB.RanCount.Should().Be(1);
    }

    [Fact]
    public async Task EnabledModules_NarrowsToListedOnly()
    {
        // NSubstitute proxies have generated names; the short-name filter
        // needs the stable class name, so we use the attribute-bearing
        // FakeAllowedModule / FakeBlockedModule defined below.
        var moduleA = new FakeAllowedModule();
        var moduleB = new FakeBlockedModule();

        var settings = new ProvisioningSettings
        {
            Stages = new Dictionary<string, StageSettings>(StringComparer.OrdinalIgnoreCase)
            {
                ["Config"] = new StageSettings
                {
                    EnabledModules = ["FakeAllowedModule"],
                },
            },
        };

        var runner = BuildRunner(settings, moduleA, moduleB);

        await runner.RunAsync(CancellationToken.None);

        moduleA.RanCount.Should().Be(1);
        moduleB.RanCount.Should().Be(0, "module is not in EnabledModules");
    }

    [Fact]
    public async Task DisabledModules_SkipsListed()
    {
        var moduleA = new FakeAllowedModule();
        var moduleB = new FakeBlockedModule();

        var settings = new ProvisioningSettings
        {
            Stages = new Dictionary<string, StageSettings>(StringComparer.OrdinalIgnoreCase)
            {
                ["Config"] = new StageSettings
                {
                    DisabledModules = ["FakeBlockedModule"],
                },
            },
        };

        var runner = BuildRunner(settings, moduleA, moduleB);

        await runner.RunAsync(CancellationToken.None);

        moduleA.RanCount.Should().Be(1);
        moduleB.RanCount.Should().Be(0, "module is in DisabledModules");
    }

    [Fact]
    public async Task EnabledAndDisabled_DisabledNarrowsTheEnabledSet()
    {
        var moduleA = new FakeAllowedModule();
        var moduleB = new FakeBlockedModule();

        var settings = new ProvisioningSettings
        {
            Stages = new Dictionary<string, StageSettings>(StringComparer.OrdinalIgnoreCase)
            {
                ["Config"] = new StageSettings
                {
                    EnabledModules = ["FakeAllowedModule", "FakeBlockedModule"],
                    DisabledModules = ["FakeBlockedModule"],
                },
            },
        };

        var runner = BuildRunner(settings, moduleA, moduleB);

        await runner.RunAsync(CancellationToken.None);

        moduleA.RanCount.Should().Be(1);
        moduleB.RanCount.Should().Be(0, "DisabledModules wins over EnabledModules");
    }

    [Fact]
    public async Task StageKey_IsCaseInsensitive()
    {
        var moduleA = new FakeAllowedModule();

        var settings = new ProvisioningSettings
        {
            Stages = new Dictionary<string, StageSettings>(StringComparer.OrdinalIgnoreCase)
            {
                ["CONFIG"] = new StageSettings
                {
                    DisabledModules = ["FakeAllowedModule"],
                },
            },
        };

        var runner = BuildRunner(settings, moduleA);

        await runner.RunAsync(CancellationToken.None);

        moduleA.RanCount.Should().Be(0, "stage key 'CONFIG' must match Stage.Config (case-insensitive)");
    }

    [Fact]
    public async Task ModuleName_MatchesWithOrWithoutModuleSuffix()
    {
        var moduleA = new FakeAllowedModule();

        // Configured as "FakeAllowed" (no suffix) — must still match.
        var settings = new ProvisioningSettings
        {
            Stages = new Dictionary<string, StageSettings>(StringComparer.OrdinalIgnoreCase)
            {
                ["Config"] = new StageSettings
                {
                    DisabledModules = ["FakeAllowed"],
                },
            },
        };

        var runner = BuildRunner(settings, moduleA);

        await runner.RunAsync(CancellationToken.None);

        moduleA.RanCount.Should().Be(0, "name match tolerates the 'Module' suffix being absent");
    }

    [Fact]
    public async Task UnknownModuleName_DoesNotFailRun()
    {
        var moduleA = new FakeAllowedModule();

        var settings = new ProvisioningSettings
        {
            Stages = new Dictionary<string, StageSettings>(StringComparer.OrdinalIgnoreCase)
            {
                ["Config"] = new StageSettings
                {
                    DisabledModules = ["ModuleThatDoesNotExist"],
                },
            },
        };

        var runner = BuildRunner(settings, moduleA);
        var outcome = await runner.RunAsync(CancellationToken.None);

        outcome.Should().BeOfType<StageRunOutcome.Success>("unknown name is a Warning, not a Failure");
        moduleA.RanCount.Should().Be(1);
    }

    // ---------- fixtures ----------

    // Attribute-bearing fakes — types are top-level so the runtime short
    // name is exactly "FakeAllowedModule" / "FakeBlockedModule" (nested type
    // names would carry the outer class prefix, which the filter wouldn't
    // match against operator config). See bottom of file.

    private static StageRunner BuildRunner(ProvisioningSettings settings, params IModule[] modules)
    {
        var locator = Substitute.For<IDataSourceLocator>();
        locator.LocateAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<DataSourceResult?>(new DataSourceResult
            {
                SourceName = "test",
                InstanceId = "instance-1",
            }));

        var pipeline = Substitute.For<IUserDataPipeline>();
        pipeline.ResolveAsync(Arg.Any<byte[]?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(ResolvedUserData.Empty(new CloudConfigModel())));

        var stateStore = Substitute.For<IStateStore>();
        stateStore.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ProvisioningState?>(null));

        var semaphoreStore = Substitute.For<ISemaphoreStore>();
        semaphoreStore.ExistsAsync(Arg.Any<string>(), Arg.Any<ModuleFrequency>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(false));
        semaphoreStore.ListPerInstanceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<string>>([]));

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
            settings,
            NullLogger<StageRunner>.Instance);
    }
}

// Top-level so the runtime short name is "FakeAllowedModule" — matches the
// allowlist/denylist filter that searches by Type.Name.
[Stage(Stage.Config, Order = 1, Frequency = ModuleFrequency.PerInstance)]
internal sealed class FakeAllowedModule : IModule
{
    public int RanCount { get; private set; }

    public Task<ModuleOutcome> ApplyAsync(
        ResolvedUserData userData,
        IModuleContext context,
        CancellationToken cancellationToken)
    {
        RanCount++;
        return Task.FromResult(ModuleOutcome.Ok());
    }
}

[Stage(Stage.Config, Order = 2, Frequency = ModuleFrequency.PerInstance)]
internal sealed class FakeBlockedModule : IModule
{
    public int RanCount { get; private set; }

    public Task<ModuleOutcome> ApplyAsync(
        ResolvedUserData userData,
        IModuleContext context,
        CancellationToken cancellationToken)
    {
        RanCount++;
        return Task.FromResult(ModuleOutcome.Ok());
    }
}
