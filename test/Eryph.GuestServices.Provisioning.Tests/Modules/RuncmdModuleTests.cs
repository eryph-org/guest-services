using AwesomeAssertions;
using Eryph.GuestServices.CloudConfig;
using Eryph.GuestServices.Provisioning.Modules;
using Eryph.GuestServices.Provisioning.UserData;
using Eryph.GuestServices.Provisioning.Windows;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using CloudConfigModel = global::Eryph.GuestServices.CloudConfig.CloudConfig;

namespace Eryph.GuestServices.Provisioning.Tests.Modules;

public sealed class RuncmdModuleTests
{
    [Fact]
    public async Task Runs_shell_commands_through_RunShellCommandAsync()
    {
        var os = Substitute.For<IWindowsOs>();
        os.RunShellCommandAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new RunCommandResult(0, "", ""));

        var module = new RuncmdModule(NullLogger<RuncmdModule>.Instance);
        var userData = ResolvedUserData.Empty(new CloudConfigModel
        {
            Runcmd =
            [
                new RuncmdEntry { IsShellCommand = true, Command = "echo hi" },
            ],
        });

        var result = await module.ApplyAsync(userData, new TestModuleContext(os), CancellationToken.None);

        result.Should().BeOfType<ModuleOutcome.Completed>();
        await os.Received().RunShellCommandAsync("echo hi", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Runs_argv_commands_through_RunArgvCommandAsync()
    {
        var os = Substitute.For<IWindowsOs>();
        os.RunArgvCommandAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new RunCommandResult(0, "", ""));

        var module = new RuncmdModule(NullLogger<RuncmdModule>.Instance);
        var userData = ResolvedUserData.Empty(new CloudConfigModel
        {
            Runcmd =
            [
                new RuncmdEntry { IsShellCommand = false, Argv = ["whoami"] },
            ],
        });

        await module.ApplyAsync(userData, new TestModuleContext(os), CancellationToken.None);

        await os.Received().RunArgvCommandAsync(
            Arg.Is<IReadOnlyList<string>>(a => a.Count == 1 && a[0] == "whoami"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Returns_RebootRequested_when_command_exits_1003()
    {
        var os = Substitute.For<IWindowsOs>();
        os.RunShellCommandAsync("first", Arg.Any<CancellationToken>())
            .Returns(new RunCommandResult(RuncmdModule.RebootRequestedExitCode, "", ""));
        os.RunShellCommandAsync("second", Arg.Any<CancellationToken>())
            .Returns(new RunCommandResult(0, "", ""));

        var module = new RuncmdModule(NullLogger<RuncmdModule>.Instance);
        var userData = ResolvedUserData.Empty(new CloudConfigModel
        {
            Runcmd =
            [
                new RuncmdEntry { IsShellCommand = true, Command = "first" },
                new RuncmdEntry { IsShellCommand = true, Command = "second" },
            ],
        });

        var result = await module.ApplyAsync(userData, new TestModuleContext(os), CancellationToken.None);

        result.Should().BeOfType<ModuleOutcome.RebootRequested>();
        // Second command must NOT be executed after the reboot signal.
        await os.DidNotReceive().RunShellCommandAsync("second", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Continues_after_non_zero_non_1003_exit_code()
    {
        var os = Substitute.For<IWindowsOs>();
        os.RunShellCommandAsync("first", Arg.Any<CancellationToken>())
            .Returns(new RunCommandResult(2, "", ""));
        os.RunShellCommandAsync("second", Arg.Any<CancellationToken>())
            .Returns(new RunCommandResult(0, "", ""));

        var module = new RuncmdModule(NullLogger<RuncmdModule>.Instance);
        var userData = ResolvedUserData.Empty(new CloudConfigModel
        {
            Runcmd =
            [
                new RuncmdEntry { IsShellCommand = true, Command = "first" },
                new RuncmdEntry { IsShellCommand = true, Command = "second" },
            ],
        });

        var result = await module.ApplyAsync(userData, new TestModuleContext(os), CancellationToken.None);

        result.Should().BeOfType<ModuleOutcome.Completed>();
        await os.Received().RunShellCommandAsync("second", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Returns_completed_when_runcmd_is_empty()
    {
        var os = Substitute.For<IWindowsOs>();
        var module = new RuncmdModule(NullLogger<RuncmdModule>.Instance);
        var userData = ResolvedUserData.Empty(new CloudConfigModel());

        var result = await module.ApplyAsync(userData, new TestModuleContext(os), CancellationToken.None);

        result.Should().BeOfType<ModuleOutcome.Completed>();
        await os.DidNotReceive().RunShellCommandAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await os.DidNotReceive().RunArgvCommandAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }
}
