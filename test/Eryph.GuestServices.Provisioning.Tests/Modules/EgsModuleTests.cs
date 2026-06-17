using AwesomeAssertions;
using Eryph.GuestServices.CloudConfig;
using Eryph.GuestServices.Core;
using Eryph.GuestServices.Provisioning.Modules;
using Eryph.GuestServices.Provisioning.Update;
using Eryph.GuestServices.Provisioning.UserData;
using Eryph.GuestServices.Provisioning.Windows;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using CloudConfigModel = global::Eryph.GuestServices.CloudConfig.CloudConfig;

namespace Eryph.GuestServices.Provisioning.Tests.Modules;

public sealed class EgsModuleTests
{
    // Default updater: no update due. Pass an explicit one to exercise the
    // update path.
    private static IEgsUpdater NoUpdate()
    {
        var updater = Substitute.For<IEgsUpdater>();
        updater.PrepareAsync(Arg.Any<EgsUpdateConfig?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UpdatePlan?>(null));
        return updater;
    }

    private static async Task<(IWindowsOs Os, ModuleOutcome Outcome)> RunAsync(
        EgsConfig? egs, IEgsUpdater? updater = null)
    {
        var os = Substitute.For<IWindowsOs>();
        var module = new EgsModule(NullLogger<EgsModule>.Instance, updater ?? NoUpdate());
        var config = new CloudConfigModel { Egs = egs };
        var outcome = await module.ApplyAsync(
            ResolvedUserData.Empty(config),
            new TestModuleContext(os),
            CancellationToken.None);
        return (os, outcome);
    }

    [Fact]
    public async Task No_egs_block_makes_no_writes()
    {
        var (os, outcome) = await RunAsync(egs: null);

        outcome.Should().BeOfType<ModuleOutcome.Completed>();
        await os.DidNotReceiveWithAnyArgs().SetServiceControlFlagAsync(default, default, default);
    }

    [Fact]
    public async Task Empty_settings_makes_no_writes()
    {
        var (os, outcome) = await RunAsync(new EgsConfig { Settings = new EgsSettingsConfig() });

        outcome.Should().BeOfType<ModuleOutcome.Completed>();
        await os.DidNotReceiveWithAnyArgs().SetServiceControlFlagAsync(default, default, default);
    }

    [Fact]
    public async Task Writes_only_the_switches_that_are_set()
    {
        // remote_access + kvp_auth supplied, provisioning omitted: the omitted
        // switch must NOT be written (three-state: null = leave untouched).
        var (os, outcome) = await RunAsync(new EgsConfig
        {
            Settings = new EgsSettingsConfig
            {
                RemoteAccess = false,
                KvpAuth = true,
            },
        });

        outcome.Should().BeOfType<ModuleOutcome.Completed>();
        await os.Received(1).SetServiceControlFlagAsync(
            ServiceControlFlag.RemoteAccess, false, Arg.Any<CancellationToken>());
        await os.Received(1).SetServiceControlFlagAsync(
            ServiceControlFlag.KvpAuth, true, Arg.Any<CancellationToken>());
        await os.DidNotReceive().SetServiceControlFlagAsync(
            ServiceControlFlag.Provisioning, Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Writes_all_three_switches_when_all_set()
    {
        var (os, outcome) = await RunAsync(new EgsConfig
        {
            Settings = new EgsSettingsConfig
            {
                RemoteAccess = true,
                Provisioning = false,
                KvpAuth = true,
            },
        });

        outcome.Should().BeOfType<ModuleOutcome.Completed>();
        await os.Received(1).SetServiceControlFlagAsync(
            ServiceControlFlag.RemoteAccess, true, Arg.Any<CancellationToken>());
        await os.Received(1).SetServiceControlFlagAsync(
            ServiceControlFlag.Provisioning, false, Arg.Any<CancellationToken>());
        await os.Received(1).SetServiceControlFlagAsync(
            ServiceControlFlag.KvpAuth, true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Settings_write_failure_surfaces_as_module_failure()
    {
        var os = Substitute.For<IWindowsOs>();
        os.SetServiceControlFlagAsync(
                ServiceControlFlag.RemoteAccess, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new UnauthorizedAccessException("registry denied"));

        var module = new EgsModule(NullLogger<EgsModule>.Instance, NoUpdate());
        var config = new CloudConfigModel
        {
            Egs = new EgsConfig { Settings = new EgsSettingsConfig { RemoteAccess = false } },
        };

        var outcome = await module.ApplyAsync(
            ResolvedUserData.Empty(config),
            new TestModuleContext(os),
            CancellationToken.None);

        outcome.Should().BeOfType<ModuleOutcome.Failed>();
    }

    [Fact]
    public async Task Staged_update_returns_UpdateRequested_before_applying_settings()
    {
        // When the updater stages a plan, the module must hand back
        // UpdateRequested (so the host swaps + restarts) and NOT yet apply
        // settings — those run on the new binary after the restart.
        var updater = Substitute.For<IEgsUpdater>();
        updater.PrepareAsync(Arg.Any<EgsUpdateConfig?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UpdatePlan?>(new UpdatePlan
            {
                StagingDirectory = @"C:\staging\0.4.0\payload",
                TargetVersion = "0.4.0",
            }));

        var (os, outcome) = await RunAsync(
            new EgsConfig
            {
                Settings = new EgsSettingsConfig { RemoteAccess = false },
                Update = new EgsUpdateConfig { Enabled = true },
            },
            updater);

        var update = outcome.Should().BeOfType<ModuleOutcome.UpdateRequested>().Subject;
        update.TargetVersion.Should().Be("0.4.0");
        update.StagingDirectory.Should().Be(@"C:\staging\0.4.0\payload");
        // Settings must NOT have been written yet — they apply post-restart.
        await os.DidNotReceiveWithAnyArgs().SetServiceControlFlagAsync(default, default, default);
    }
}
