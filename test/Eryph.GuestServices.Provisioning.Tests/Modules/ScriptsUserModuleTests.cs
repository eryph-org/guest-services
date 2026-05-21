using System.Text;
using AwesomeAssertions;
using Eryph.GuestServices.Provisioning.Configuration;
using Eryph.GuestServices.Provisioning.Modules;
using Eryph.GuestServices.Provisioning.Stages;
using Eryph.GuestServices.Provisioning.UserData;
using Eryph.GuestServices.Provisioning.Windows;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Eryph.GuestServices.Provisioning.Tests.Modules;

public sealed class ScriptsUserModuleTests
{
    private static readonly ProvisioningSettings TestSettings = new()
    {
        Scripts = new ScriptSettings
        {
            PerInstanceDirectory = @"C:\temp\eryph-scripts-test",
        },
    };

    [Fact]
    public async Task ApplyAsync_NoScripts_NoOp()
    {
        var os = Substitute.For<IWindowsOs>();
        var module = new ScriptsUserModule(NullLogger<ScriptsUserModule>.Instance, TestSettings);

        var resolved = ResolvedUserData.Empty(new global::Eryph.GuestServices.CloudConfig.CloudConfig());
        var outcome = await module.ApplyAsync(resolved, new TestModuleContext(os), CancellationToken.None);

        outcome.Should().BeOfType<ModuleOutcome.Completed>();
        await os.DidNotReceive().WriteFileAsync(Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        await os.DidNotReceive().RunArgvCommandAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApplyAsync_PowerShellScript_StagesAndExecutesViaPowerShell()
    {
        var os = Substitute.For<IWindowsOs>();
        os.RunArgvCommandAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new RunCommandResult(0, "ok", ""));
        var module = new ScriptsUserModule(NullLogger<ScriptsUserModule>.Instance, TestSettings);

        var script = new ScriptPayload(ScriptKind.PowerShell, Encoding.UTF8.GetBytes("Write-Host hi"), "do-thing.ps1");
        var resolved = ResolvedUserData.Empty(new global::Eryph.GuestServices.CloudConfig.CloudConfig())
            with { Scripts = [script] };

        var outcome = await module.ApplyAsync(resolved, new TestModuleContext(os), CancellationToken.None);

        outcome.Should().BeOfType<ModuleOutcome.Completed>();
        await os.Received().WriteFileAsync(
            Arg.Is<string>(p => p.EndsWith("001-do-thing.ps1")),
            Arg.Any<byte[]>(),
            false,
            Arg.Any<CancellationToken>());
        await os.Received().RunArgvCommandAsync(
            Arg.Is<IReadOnlyList<string>>(argv =>
                argv[0] == "powershell.exe" && argv.Contains("-File")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApplyAsync_ScriptExitCode1003_ReturnsRebootRequested()
    {
        var os = Substitute.For<IWindowsOs>();
        os.RunArgvCommandAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new RunCommandResult(1003, "", ""));
        var module = new ScriptsUserModule(NullLogger<ScriptsUserModule>.Instance, TestSettings);

        var script = new ScriptPayload(ScriptKind.PowerShell, Encoding.UTF8.GetBytes("# reboot"), null);
        var resolved = ResolvedUserData.Empty(new global::Eryph.GuestServices.CloudConfig.CloudConfig())
            with { Scripts = [script] };

        var outcome = await module.ApplyAsync(resolved, new TestModuleContext(os), CancellationToken.None);

        outcome.Should().BeOfType<ModuleOutcome.RebootRequested>();
    }

    [Fact]
    public async Task ApplyAsync_MultipleScripts_StagesWithSequentialNamesAndIndices()
    {
        var os = Substitute.For<IWindowsOs>();
        os.RunArgvCommandAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new RunCommandResult(0, "", ""));
        var module = new ScriptsUserModule(NullLogger<ScriptsUserModule>.Instance, TestSettings);

        var resolved = ResolvedUserData.Empty(new global::Eryph.GuestServices.CloudConfig.CloudConfig())
            with
            {
                Scripts =
                [
                    new ScriptPayload(ScriptKind.PowerShell, Encoding.UTF8.GetBytes("# 1"), "first.ps1"),
                    new ScriptPayload(ScriptKind.Cmd, Encoding.UTF8.GetBytes("rem 2"), "second.cmd"),
                    new ScriptPayload(ScriptKind.PowerShell, Encoding.UTF8.GetBytes("# 3"), null),
                ],
            };

        var outcome = await module.ApplyAsync(resolved, new TestModuleContext(os), CancellationToken.None);

        outcome.Should().BeOfType<ModuleOutcome.Completed>();
        await os.Received().WriteFileAsync(
            Arg.Is<string>(p => p.EndsWith("001-first.ps1")),
            Arg.Any<byte[]>(), false, Arg.Any<CancellationToken>());
        await os.Received().WriteFileAsync(
            Arg.Is<string>(p => p.EndsWith("002-second.cmd")),
            Arg.Any<byte[]>(), false, Arg.Any<CancellationToken>());
        await os.Received().WriteFileAsync(
            Arg.Is<string>(p => p.EndsWith("003-script-3.ps1")),
            Arg.Any<byte[]>(), false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Module_HasFinalStageAttribute()
    {
        var attr = typeof(ScriptsUserModule).GetCustomAttributes(typeof(StageAttribute), inherit: false);
        attr.Should().HaveCount(1);
        ((StageAttribute)attr[0]).Stage.Should().Be(Stage.Final);
    }
}
