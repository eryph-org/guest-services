using AwesomeAssertions;
using Eryph.GuestServices.CloudConfig;
using Eryph.GuestServices.Provisioning.Modules;
using Eryph.GuestServices.Provisioning.Stages;
using Eryph.GuestServices.Provisioning.UserData;
using Eryph.GuestServices.Provisioning.Windows;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using CloudConfigModel = global::Eryph.GuestServices.CloudConfig.CloudConfig;

namespace Eryph.GuestServices.Provisioning.Tests.Modules;

public sealed class PowerStateModuleTests
{
    [Fact]
    public async Task Missing_block_is_a_no_op()
    {
        var os = Substitute.For<IWindowsOs>();
        var module = new PowerStateModule(NullLogger<PowerStateModule>.Instance);

        var result = await module.ApplyAsync(
            ResolvedUserData.Empty(new CloudConfigModel()),
            new TestModuleContext(os),
            CancellationToken.None);

        result.Should().BeOfType<ModuleOutcome.Completed>();
        await os.DidNotReceive().RequestPowerStateAsync(Arg.Any<PowerStateRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Default_mode_is_reboot_with_minimum_delay()
    {
        // power_state: {} (block present, no fields) → reboot now, but
        // clamped to the StageRunner-cleanup buffer so the per-instance
        // semaphore can flush before Windows tears the agent down.
        var os = Substitute.For<IWindowsOs>();
        var module = new PowerStateModule(NullLogger<PowerStateModule>.Instance);
        var config = new CloudConfigModel { PowerState = new PowerStateConfig() };

        await module.ApplyAsync(
            ResolvedUserData.Empty(config),
            new TestModuleContext(os),
            CancellationToken.None);

        await os.Received(1).RequestPowerStateAsync(
            Arg.Is<PowerStateRequest>(r =>
                r.Action == PowerStateAction.Reboot && r.DelaySeconds >= 5),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Poweroff_maps_to_Poweroff_action()
    {
        var os = Substitute.For<IWindowsOs>();
        var module = new PowerStateModule(NullLogger<PowerStateModule>.Instance);
        var config = new CloudConfigModel
        {
            PowerState = new PowerStateConfig { Mode = "poweroff", Message = "done" },
        };

        await module.ApplyAsync(
            ResolvedUserData.Empty(config),
            new TestModuleContext(os),
            CancellationToken.None);

        await os.Received(1).RequestPowerStateAsync(
            Arg.Is<PowerStateRequest>(r =>
                r.Action == PowerStateAction.Poweroff && r.Message == "done"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Cbi_style_shutdown_alias_is_accepted_as_poweroff()
    {
        // cloudbase-init operators sometimes write `mode: shutdown` —
        // accept it as an alias for poweroff so cross-tool YAML works.
        var os = Substitute.For<IWindowsOs>();
        var module = new PowerStateModule(NullLogger<PowerStateModule>.Instance);
        var config = new CloudConfigModel
        {
            PowerState = new PowerStateConfig { Mode = "shutdown" },
        };

        await module.ApplyAsync(
            ResolvedUserData.Empty(config),
            new TestModuleContext(os),
            CancellationToken.None);

        await os.Received(1).RequestPowerStateAsync(
            Arg.Is<PowerStateRequest>(r => r.Action == PowerStateAction.Poweroff),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Halt_mode_maps_to_Halt_action()
    {
        // Halt has no clean Windows analogue. The OS layer translates to
        // hibernate; we just confirm the enum value is propagated so the
        // OS layer can decide.
        var os = Substitute.For<IWindowsOs>();
        var module = new PowerStateModule(NullLogger<PowerStateModule>.Instance);
        var config = new CloudConfigModel
        {
            PowerState = new PowerStateConfig { Mode = "halt" },
        };

        await module.ApplyAsync(
            ResolvedUserData.Empty(config),
            new TestModuleContext(os),
            CancellationToken.None);

        await os.Received(1).RequestPowerStateAsync(
            Arg.Is<PowerStateRequest>(r => r.Action == PowerStateAction.Halt),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Unknown_mode_returns_Failed()
    {
        var os = Substitute.For<IWindowsOs>();
        var module = new PowerStateModule(NullLogger<PowerStateModule>.Instance);
        var config = new CloudConfigModel
        {
            PowerState = new PowerStateConfig { Mode = "klingon" },
        };

        var result = await module.ApplyAsync(
            ResolvedUserData.Empty(config),
            new TestModuleContext(os),
            CancellationToken.None);

        result.Should().BeOfType<ModuleOutcome.Failed>()
            .Which.Reason.Should().Contain("klingon");
        await os.DidNotReceive().RequestPowerStateAsync(Arg.Any<PowerStateRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Condition_literal_false_skips_the_shutdown()
    {
        var os = Substitute.For<IWindowsOs>();
        var module = new PowerStateModule(NullLogger<PowerStateModule>.Instance);
        var config = new CloudConfigModel
        {
            PowerState = new PowerStateConfig { Mode = "reboot", Condition = BoolOrString.FromBool(false) },
        };

        var result = await module.ApplyAsync(
            ResolvedUserData.Empty(config),
            new TestModuleContext(os),
            CancellationToken.None);

        result.Should().BeOfType<ModuleOutcome.Completed>();
        await os.DidNotReceive().RequestPowerStateAsync(Arg.Any<PowerStateRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Condition_literal_true_proceeds()
    {
        var os = Substitute.For<IWindowsOs>();
        var module = new PowerStateModule(NullLogger<PowerStateModule>.Instance);
        var config = new CloudConfigModel
        {
            PowerState = new PowerStateConfig { Mode = "reboot", Condition = BoolOrString.FromBool(true) },
        };

        await module.ApplyAsync(
            ResolvedUserData.Empty(config),
            new TestModuleContext(os),
            CancellationToken.None);

        await os.Received(1).RequestPowerStateAsync(Arg.Any<PowerStateRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Condition_Empty_BoolOrString_proceeds()
    {
        // Default-constructed BoolOrString (operator omitted condition:)
        // must default to "proceed" — matches cloud-init's behaviour when
        // the key is absent.
        var os = Substitute.For<IWindowsOs>();
        var module = new PowerStateModule(NullLogger<PowerStateModule>.Instance);
        var config = new CloudConfigModel
        {
            PowerState = new PowerStateConfig { Mode = "reboot", Condition = BoolOrString.Empty },
        };

        await module.ApplyAsync(
            ResolvedUserData.Empty(config),
            new TestModuleContext(os),
            CancellationToken.None);

        await os.Received(1).RequestPowerStateAsync(Arg.Any<PowerStateRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Condition_command_exit_0_proceeds_non_zero_skips()
    {
        // Mirrors cloud-init: the condition string is run as a shell
        // command; exit 0 means "go ahead and reboot", anything else
        // means "skip the action".
        var os = Substitute.For<IWindowsOs>();
        os.RunShellCommandAsync("ok-cmd", Arg.Any<CancellationToken>())
            .Returns(new RunCommandResult(0, "", ""));
        os.RunShellCommandAsync("fail-cmd", Arg.Any<CancellationToken>())
            .Returns(new RunCommandResult(1, "", "err"));
        var module = new PowerStateModule(NullLogger<PowerStateModule>.Instance);

        await module.ApplyAsync(
            ResolvedUserData.Empty(new CloudConfigModel
            {
                PowerState = new PowerStateConfig { Mode = "reboot", Condition = BoolOrString.FromString("ok-cmd") },
            }),
            new TestModuleContext(os),
            CancellationToken.None);
        await os.Received(1).RequestPowerStateAsync(Arg.Any<PowerStateRequest>(), Arg.Any<CancellationToken>());

        os.ClearReceivedCalls();

        await module.ApplyAsync(
            ResolvedUserData.Empty(new CloudConfigModel
            {
                PowerState = new PowerStateConfig { Mode = "reboot", Condition = BoolOrString.FromString("fail-cmd") },
            }),
            new TestModuleContext(os),
            CancellationToken.None);
        await os.DidNotReceive().RequestPowerStateAsync(Arg.Any<PowerStateRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Returns_Completed_not_RebootRequested()
    {
        // Critical regression pin: returning RebootRequested instead of
        // Completed would mean the post-reboot run re-enters the module
        // and schedules ANOTHER reboot — infinite loop. The shutdown is
        // already scheduled at the OS level; the module's job is done.
        var os = Substitute.For<IWindowsOs>();
        var module = new PowerStateModule(NullLogger<PowerStateModule>.Instance);

        var result = await module.ApplyAsync(
            ResolvedUserData.Empty(new CloudConfigModel
            {
                PowerState = new PowerStateConfig { Mode = "reboot" },
            }),
            new TestModuleContext(os),
            CancellationToken.None);

        result.Should().BeOfType<ModuleOutcome.Completed>();
    }

    [Fact]
    public async Task OS_exception_surfaces_as_Failed()
    {
        var os = Substitute.For<IWindowsOs>();
        os.RequestPowerStateAsync(Arg.Any<PowerStateRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsyncForAnyArgs(new InvalidOperationException("shutdown.exe exit 1"));
        var module = new PowerStateModule(NullLogger<PowerStateModule>.Instance);

        var result = await module.ApplyAsync(
            ResolvedUserData.Empty(new CloudConfigModel
            {
                PowerState = new PowerStateConfig { Mode = "reboot" },
            }),
            new TestModuleContext(os),
            CancellationToken.None);

        result.Should().BeOfType<ModuleOutcome.Failed>()
            .Which.Reason.Should().Contain("shutdown.exe");
    }

    [Fact]
    public void Stage_attribute_is_Final_LastOrder_PerInstance()
    {
        // Final / Order = int.MaxValue - 1 / PerInstance — runs LAST so
        // every other Final module has had its turn. PerInstance so the
        // per-instance semaphore prevents looping on the post-reboot run.
        var attr = typeof(PowerStateModule)
            .GetCustomAttributes(typeof(StageAttribute), inherit: false)
            .OfType<StageAttribute>()
            .Single();

        attr.Stage.Should().Be(Stage.Final);
        attr.Order.Should().Be(int.MaxValue - 1);
        attr.Frequency.Should().Be(ModuleFrequency.PerInstance);
    }

    // Grammar tests for delay parsing live in
    // Eryph.GuestServices.CloudConfig.Tests/PowerStateGrammarTests.cs —
    // the parser is now in the model library so the CLI validate path
    // exercises it too.
}
