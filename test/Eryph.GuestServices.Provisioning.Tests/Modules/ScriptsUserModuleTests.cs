using System.Text;
using AwesomeAssertions;
using Eryph.GuestServices.Provisioning.Configuration;
using Eryph.GuestServices.Provisioning.Modules;
using Eryph.GuestServices.Provisioning.Reporting;
using Eryph.GuestServices.Provisioning.Reporting.Events;
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

    private static ScriptsUserModule CreateModule(
        IReportingDispatcher? reporter = null,
        IScriptCheckpointStore? checkpointStore = null) =>
        new(NullLogger<ScriptsUserModule>.Instance,
            TestSettings,
            reporter ?? Substitute.For<IReportingDispatcher>(),
            checkpointStore ?? new InMemoryScriptCheckpointStore());

    // Creates an IWindowsOs mock with deterministic %VAR% expansion so the
    // module's path assembly is host-OS independent. Without this the real
    // Environment.ExpandEnvironmentVariables (now routed through IWindowsOs)
    // would no-op on a non-Windows host and the staged-script / log paths
    // would lose their %ProgramData% root.
    private static IWindowsOs CreateOs()
    {
        var os = Substitute.For<IWindowsOs>();
        // The module passes PerInstanceDirectory (no env vars) and the logs
        // directory ("%ProgramData%\eryph\provisioning\logs") through
        // ExpandEnvironmentVariables. Pin both to deterministic Windows paths
        // so the staged-script / log path assembly is host-OS independent.
        os.ExpandEnvironmentVariables(TestSettings.Scripts.PerInstanceDirectory)
            .Returns(TestSettings.Scripts.PerInstanceDirectory);
        os.ExpandEnvironmentVariables(@"%ProgramData%\eryph\provisioning\logs")
            .Returns(@"C:\ProgramData\eryph\provisioning\logs");
        return os;
    }

    [Fact]
    public async Task ApplyAsync_NoScripts_NoOp()
    {
        var os = CreateOs();
        var module = CreateModule();

        var resolved = ResolvedUserData.Empty(new global::Eryph.GuestServices.CloudConfig.CloudConfig());
        var outcome = await module.ApplyAsync(resolved, new TestModuleContext(os), CancellationToken.None);

        outcome.Should().BeOfType<ModuleOutcome.Completed>();
        await os.DidNotReceive().WriteFileAsync(Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        await os.DidNotReceive().RunArgvCommandAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApplyAsync_PowerShellScript_StagesAndExecutesViaPowerShell()
    {
        var os = CreateOs();
        os.RunArgvCommandAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new RunCommandResult(0, "ok", ""));
        var module = CreateModule();

        var script = new ScriptPayload(ScriptKind.PowerShell, Encoding.UTF8.GetBytes("Write-Host hi"), "do-thing.ps1");
        var resolved = ResolvedUserData.Empty(new global::Eryph.GuestServices.CloudConfig.CloudConfig())
            with { Scripts = [script] };

        var outcome = await module.ApplyAsync(resolved, new TestModuleContext(os), CancellationToken.None);

        outcome.Should().BeOfType<ModuleOutcome.Completed>();
        await os.Received().WriteFileAsync(
            Arg.Is<string>(p => p.EndsWith(@"\001-do-thing.ps1")),
            Arg.Any<byte[]>(),
            false,
            Arg.Any<CancellationToken>());
        await os.Received().RunArgvCommandAsync(
            Arg.Is<IReadOnlyList<string>>(argv =>
                argv[0] == "powershell.exe" && argv.Contains("-Command")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApplyAsync_ScriptExitCode1003_ReturnsRebootRequested()
    {
        var os = CreateOs();
        os.RunArgvCommandAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new RunCommandResult(1003, "", ""));
        var module = CreateModule();

        var script = new ScriptPayload(ScriptKind.PowerShell, Encoding.UTF8.GetBytes("# reboot"), null);
        var resolved = ResolvedUserData.Empty(new global::Eryph.GuestServices.CloudConfig.CloudConfig())
            with { Scripts = [script] };

        var outcome = await module.ApplyAsync(resolved, new TestModuleContext(os), CancellationToken.None);

        outcome.Should().BeOfType<ModuleOutcome.RebootRequested>();
    }

    [Fact]
    public async Task ApplyAsync_MultipleScripts_StagesWithSequentialNamesAndIndices()
    {
        var os = CreateOs();
        os.RunArgvCommandAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new RunCommandResult(0, "", ""));
        var module = CreateModule();

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
            Arg.Is<string>(p => p.EndsWith(@"\001-first.ps1")),
            Arg.Any<byte[]>(), false, Arg.Any<CancellationToken>());
        await os.Received().WriteFileAsync(
            Arg.Is<string>(p => p.EndsWith(@"\002-second.cmd")),
            Arg.Any<byte[]>(), false, Arg.Any<CancellationToken>());
        await os.Received().WriteFileAsync(
            Arg.Is<string>(p => p.EndsWith(@"\003-script.ps1")),
            Arg.Any<byte[]>(), false, Arg.Any<CancellationToken>());
    }

    // Regression: real eryph gene fodder (enable_rd.ps1) ships as
    //   Content-Type: text/x-shellscript
    //   Content-Disposition: attachment; filename="enable_rd.ps1"
    //   <body starts with Set-ItemProperty ... — NO shebang>
    // Under the OLD shebang-led detection this was silently classified as
    // ScriptKind.Other and dropped. cbi runs it as PowerShell because the
    // filename extension is .ps1; we must do the same. This test fails
    // under the old code and passes under the filename-led detector.
    [Fact]
    public async Task ApplyAsync_CbiShape_ShellscriptPartWithPs1FilenameAndNoShebang_DispatchesAsPowerShell()
    {
        var os = CreateOs();
        os.RunArgvCommandAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new RunCommandResult(0, "", ""));
        var module = CreateModule();

        // Simulate the user-data pipeline: a text/x-shellscript part with a
        // .ps1 filename and a body that has no shebang. The ShellScriptPartHandler
        // would classify this as ScriptKind.PowerShell via the filename-led
        // detector and append it to ResolvedUserData.Scripts.
        var body = Encoding.UTF8.GetBytes(
            "Set-ItemProperty -Path 'HKLM:\\System\\CurrentControlSet\\Control\\Terminal Server' "
            + "-Name 'fDenyTSConnections' -Value 0\n");
        var script = new ScriptPayload(ScriptKind.PowerShell, body, "enable_rd.ps1");
        var resolved = ResolvedUserData.Empty(new global::Eryph.GuestServices.CloudConfig.CloudConfig())
            with { Scripts = [script] };

        var outcome = await module.ApplyAsync(resolved, new TestModuleContext(os), CancellationToken.None);

        outcome.Should().BeOfType<ModuleOutcome.Completed>();
        await os.Received().WriteFileAsync(
            Arg.Is<string>(p => p.EndsWith(@"\001-enable_rd.ps1")),
            Arg.Any<byte[]>(),
            false,
            Arg.Any<CancellationToken>());
        await os.Received().RunArgvCommandAsync(
            Arg.Is<IReadOnlyList<string>>(argv =>
                argv[0] == "powershell.exe"
                && argv.Contains("-NoProfile")
                && argv.Contains("-NonInteractive")
                && argv.Contains("-ExecutionPolicy")
                && argv.Contains("Bypass")
                && argv.Contains("-Command")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApplyAsync_ScriptKindOther_IsSkippedWithoutExecution()
    {
        var os = CreateOs();
        var module = CreateModule();

        var script = new ScriptPayload(ScriptKind.Other, Encoding.UTF8.GetBytes("#!/bin/bash\necho hi\n"), "garbage.sh");
        var resolved = ResolvedUserData.Empty(new global::Eryph.GuestServices.CloudConfig.CloudConfig())
            with { Scripts = [script] };

        var outcome = await module.ApplyAsync(resolved, new TestModuleContext(os), CancellationToken.None);

        outcome.Should().BeOfType<ModuleOutcome.Completed>();
        await os.DidNotReceive().WriteFileAsync(
            Arg.Is<string>(p => p.Contains("garbage")),
            Arg.Any<byte[]>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        await os.DidNotReceive().RunArgvCommandAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApplyAsync_OnSuccess_EmitsProgressEventAndWritesPerScriptLog()
    {
        var os = CreateOs();
        os.RunArgvCommandAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new RunCommandResult(0, "captured-stdout", "captured-stderr"));
        var reporter = Substitute.For<IReportingDispatcher>();
        var module = CreateModule(reporter);

        var script = new ScriptPayload(ScriptKind.PowerShell, Encoding.UTF8.GetBytes("# x"), "hello.ps1");
        var resolved = ResolvedUserData.Empty(new global::Eryph.GuestServices.CloudConfig.CloudConfig())
            with { Scripts = [script] };

        await module.ApplyAsync(resolved, new TestModuleContext(os), CancellationToken.None);

        // Per-script log under %ProgramData%\eryph\provisioning\logs.
        await os.Received().WriteFileAsync(
            Arg.Is<string>(p => p.EndsWith(@"\logs\001-hello.ps1.log")),
            Arg.Is<byte[]>(b => Encoding.UTF8.GetString(b).Contains("captured-stdout")
                                && Encoding.UTF8.GetString(b).Contains("captured-stderr")),
            false,
            Arg.Any<CancellationToken>());

        await reporter.Received().EmitAsync(
            Arg.Is<ReportingEvent>(e =>
                (e as ReportingEvent.Progress) != null
                && ((ReportingEvent.Progress)e).Message.Contains("001-hello.ps1")
                && ((ReportingEvent.Progress)e).Message.Contains("captured-stdout")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Module_HasFinalStageAttribute()
    {
        var attr = typeof(ScriptsUserModule).GetCustomAttributes(typeof(StageAttribute), inherit: false);
        attr.Should().HaveCount(1);
        ((StageAttribute)attr[0]).Stage.Should().Be(Stage.Final);
    }
}
