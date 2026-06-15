using AwesomeAssertions;
using Eryph.GuestServices.CloudConfig;
using Eryph.GuestServices.Core;
using Eryph.GuestServices.Provisioning.Modules;
using Eryph.GuestServices.Provisioning.UserData;
using Eryph.GuestServices.Provisioning.Windows;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using CloudConfigModel = global::Eryph.GuestServices.CloudConfig.CloudConfig;

namespace Eryph.GuestServices.Provisioning.Tests.Modules;

public sealed class EgsModuleTests
{
    private static async Task<(IWindowsOs Os, ModuleOutcome Outcome)> RunAsync(EgsConfig? egs)
    {
        var os = Substitute.For<IWindowsOs>();
        var module = new EgsModule(NullLogger<EgsModule>.Instance);
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

        var module = new EgsModule(NullLogger<EgsModule>.Instance);
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
}
