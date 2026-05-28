using System.Text;
using AwesomeAssertions;
using Eryph.GuestServices.Provisioning.Configuration;
using Eryph.GuestServices.Provisioning.Modules;
using Eryph.GuestServices.Provisioning.Reporting;
using Eryph.GuestServices.Provisioning.Stages;
using Eryph.GuestServices.Provisioning.UserData;
using Eryph.GuestServices.Provisioning.Windows;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Eryph.GuestServices.Provisioning.Tests.Modules;

/// <summary>
/// Regression tests for docs/bugs/0001-scriptsusermodule-skips-queue-after-reboot.md
/// (ScriptsUserModule half).
///
/// Pre-fix the module re-iterated <c>userData.Scripts</c> from index 0 on
/// every invocation. Combined with the StageRunner gate bug, scripts
/// declared AFTER a 1003-returning script were silently dropped.
///
/// Now the module persists a per-script checkpoint (ordinal + body-hash).
/// Resume after a reboot skips already-executed scripts and continues with
/// the next one, mirroring cbi's plugin-runner behaviour.
/// </summary>
public sealed class ScriptsUserModuleCheckpointTests
{
    private static readonly ProvisioningSettings TestSettings = new()
    {
        Scripts = new ScriptSettings { PerInstanceDirectory = @"C:\temp\eryph-scripts-test" },
    };

    private static ScriptsUserModule CreateModule(
        IScriptCheckpointStore checkpointStore,
        ProvisioningSettings? settings = null) =>
        new(NullLogger<ScriptsUserModule>.Instance,
            settings ?? TestSettings,
            Substitute.For<IReportingDispatcher>(),
            checkpointStore);

    [Fact]
    public async Task Resume_after_1003_skips_already_executed_scripts_and_runs_the_next_one()
    {
        // Mirror the bug-doc repro: script #3 returns 1003 on first run; on
        // the resume pass scripts #1, #2, #3 must be skipped (checkpoint
        // matches) and script #4 must execute.
        var os = Substitute.For<IWindowsOs>();
        // Round 1: script 3 returns 1003; the others return 0. We track by
        // file path argument — RunArgvCommandAsync gets the staged path on
        // index "-File <path>" or similar.
        os.RunArgvCommandAsync(
            Arg.Is<IReadOnlyList<string>>(argv => argv.Any(s => s.Contains("003-three.ps1"))),
            Arg.Any<IReadOnlyDictionary<string, string>>(),

            Arg.Any<CancellationToken>()).Returns(new RunCommandResult(1003, "", ""));
        os.RunArgvCommandAsync(
            Arg.Is<IReadOnlyList<string>>(argv => argv.Any(s => s.Contains("001-one.ps1") || s.Contains("002-two.ps1") || s.Contains("004-four.ps1"))),
            Arg.Any<IReadOnlyDictionary<string, string>>(),

            Arg.Any<CancellationToken>()).Returns(new RunCommandResult(0, "", ""));

        var checkpoint = new InMemoryScriptCheckpointStore();
        var module = CreateModule(checkpoint);

        var resolved = ResolvedUserData.Empty(new global::Eryph.GuestServices.CloudConfig.CloudConfig())
            with
            {
                Scripts =
                [
                    new ScriptPayload(ScriptKind.PowerShell, Encoding.UTF8.GetBytes("# 1"), "one.ps1"),
                    new ScriptPayload(ScriptKind.PowerShell, Encoding.UTF8.GetBytes("# 2"), "two.ps1"),
                    new ScriptPayload(ScriptKind.PowerShell, Encoding.UTF8.GetBytes("# 3"), "three.ps1"),
                    new ScriptPayload(ScriptKind.PowerShell, Encoding.UTF8.GetBytes("# 4"), "four.ps1"),
                ],
            };

        var ctx = new TestModuleContext(os);

        // Round 1: scripts 1, 2, 3 execute; 3 returns 1003 → Reboot. Script 4 not reached.
        var outcome1 = await module.ApplyAsync(resolved, ctx, CancellationToken.None);
        outcome1.Should().BeOfType<ModuleOutcome.RebootRequested>();
        await os.Received().RunArgvCommandAsync(
            Arg.Is<IReadOnlyList<string>>(argv => argv.Any(s => s.Contains("001-one.ps1"))),
            Arg.Any<IReadOnlyDictionary<string, string>>(),

            Arg.Any<CancellationToken>());
        await os.Received().RunArgvCommandAsync(
            Arg.Is<IReadOnlyList<string>>(argv => argv.Any(s => s.Contains("003-three.ps1"))),
            Arg.Any<IReadOnlyDictionary<string, string>>(),

            Arg.Any<CancellationToken>());
        await os.DidNotReceive().RunArgvCommandAsync(
            Arg.Is<IReadOnlyList<string>>(argv => argv.Any(s => s.Contains("004-four.ps1"))),
            Arg.Any<IReadOnlyDictionary<string, string>>(),

            Arg.Any<CancellationToken>());

        // Round 2 (post-reboot resume): scripts 1, 2, 3 are checkpointed →
        // skipped. Script 4 executes for the first time.
        os.ClearReceivedCalls();
        // Round-2 calls all return 0 (Hyper-V finished installing across the reboot).
        os.RunArgvCommandAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(new RunCommandResult(0, "", ""));

        var outcome2 = await module.ApplyAsync(resolved, ctx, CancellationToken.None);

        outcome2.Should().BeOfType<ModuleOutcome.Completed>();
        // Scripts 1, 2, 3 must NOT be re-staged or re-executed.
        await os.DidNotReceive().WriteFileAsync(
            Arg.Is<string>(p => p.EndsWith(@"\001-one.ps1")
                                || p.EndsWith(@"\002-two.ps1")
                                || p.EndsWith(@"\003-three.ps1")),
            Arg.Any<byte[]>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        await os.DidNotReceive().RunArgvCommandAsync(
            Arg.Is<IReadOnlyList<string>>(argv => argv.Any(s => s.Contains("001-one.ps1")
                                                                || s.Contains("002-two.ps1")
                                                                || s.Contains("003-three.ps1"))),
            Arg.Any<IReadOnlyDictionary<string, string>>(),

            Arg.Any<CancellationToken>());
        // Script 4 must run.
        await os.Received().RunArgvCommandAsync(
            Arg.Is<IReadOnlyList<string>>(argv => argv.Any(s => s.Contains("004-four.ps1"))),
            Arg.Any<IReadOnlyDictionary<string, string>>(),

            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Exit_1001_marks_script_completed_and_returns_script_driven_reboot()
    {
        // 1001 = "reboot, but this script is done." Checkpoint must record
        // the executed entry so resume skips it; reboot outcome must be
        // script-driven so the per-module cap stays out of the way.
        var os = Substitute.For<IWindowsOs>();
        os.RunArgvCommandAsync(
                Arg.Is<IReadOnlyList<string>>(argv => argv.Any(s => s.Contains("001-installer.ps1"))),
                Arg.Any<IReadOnlyDictionary<string, string>>(),

                Arg.Any<CancellationToken>())
            .Returns(new RunCommandResult(1001, "", ""));
        os.RunArgvCommandAsync(
                Arg.Is<IReadOnlyList<string>>(argv => argv.Any(s => s.Contains("002-next.ps1"))),
                Arg.Any<IReadOnlyDictionary<string, string>>(),

                Arg.Any<CancellationToken>())
            .Returns(new RunCommandResult(0, "", ""));

        var checkpoint = new InMemoryScriptCheckpointStore();
        var module = CreateModule(checkpoint);
        var ctx = new TestModuleContext(os);

        var resolved = ResolvedUserData.Empty(new global::Eryph.GuestServices.CloudConfig.CloudConfig())
            with
            {
                Scripts =
                [
                    new ScriptPayload(ScriptKind.PowerShell, Encoding.UTF8.GetBytes("# 1001"), "installer.ps1"),
                    new ScriptPayload(ScriptKind.PowerShell, Encoding.UTF8.GetBytes("# next"), "next.ps1"),
                ],
            };

        var outcome1 = await module.ApplyAsync(resolved, ctx, CancellationToken.None);
        var reboot = outcome1.Should().BeOfType<ModuleOutcome.RebootRequested>().Subject;
        reboot.IsScriptDriven.Should().BeTrue();
        reboot.Reason.Should().Contain("exit 1001");

        // Resume: installer.ps1 is marked executed, next.ps1 runs.
        os.ClearReceivedCalls();
        var outcome2 = await module.ApplyAsync(resolved, ctx, CancellationToken.None);
        outcome2.Should().BeOfType<ModuleOutcome.Completed>();
        await os.DidNotReceive().RunArgvCommandAsync(
            Arg.Is<IReadOnlyList<string>>(argv => argv.Any(s => s.Contains("001-installer.ps1"))),
            Arg.Any<IReadOnlyDictionary<string, string>>(),

            Arg.Any<CancellationToken>());
        await os.Received().RunArgvCommandAsync(
            Arg.Is<IReadOnlyList<string>>(argv => argv.Any(s => s.Contains("002-next.ps1"))),
            Arg.Any<IReadOnlyDictionary<string, string>>(),

            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Exit_1003_returns_script_driven_reboot_so_module_cap_is_bypassed()
    {
        var os = Substitute.For<IWindowsOs>();
        os.RunArgvCommandAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(new RunCommandResult(1003, "", ""));

        var checkpoint = new InMemoryScriptCheckpointStore();
        var module = CreateModule(checkpoint);
        var ctx = new TestModuleContext(os);

        var resolved = ResolvedUserData.Empty(new global::Eryph.GuestServices.CloudConfig.CloudConfig())
            with { Scripts = [new ScriptPayload(ScriptKind.PowerShell, Encoding.UTF8.GetBytes("# 1003"), "wants-reboot.ps1")] };

        var outcome = await module.ApplyAsync(resolved, ctx, CancellationToken.None);

        var reboot = outcome.Should().BeOfType<ModuleOutcome.RebootRequested>().Subject;
        reboot.IsScriptDriven.Should().BeTrue(
            "scripts plugin reboots are user-script-driven so the per-module cap does not gate multi-stage installers");
    }

    [Fact]
    public async Task Exit_1002_is_treated_as_unsupported_and_module_continues()
    {
        // 1002 ("re-run on next boot, no reboot") has no eryph equivalent —
        // we log a warning and continue with the next script.
        var os = Substitute.For<IWindowsOs>();
        os.RunArgvCommandAsync(
                Arg.Is<IReadOnlyList<string>>(argv => argv.Any(s => s.Contains("001-deferred.ps1"))),
                Arg.Any<IReadOnlyDictionary<string, string>>(),

                Arg.Any<CancellationToken>())
            .Returns(new RunCommandResult(1002, "", ""));
        os.RunArgvCommandAsync(
                Arg.Is<IReadOnlyList<string>>(argv => argv.Any(s => s.Contains("002-next.ps1"))),
                Arg.Any<IReadOnlyDictionary<string, string>>(),

                Arg.Any<CancellationToken>())
            .Returns(new RunCommandResult(0, "", ""));

        var checkpoint = new InMemoryScriptCheckpointStore();
        var module = CreateModule(checkpoint);
        var ctx = new TestModuleContext(os);

        var resolved = ResolvedUserData.Empty(new global::Eryph.GuestServices.CloudConfig.CloudConfig())
            with
            {
                Scripts =
                [
                    new ScriptPayload(ScriptKind.PowerShell, Encoding.UTF8.GetBytes("# 1002"), "deferred.ps1"),
                    new ScriptPayload(ScriptKind.PowerShell, Encoding.UTF8.GetBytes("# next"), "next.ps1"),
                ],
            };

        var outcome = await module.ApplyAsync(resolved, ctx, CancellationToken.None);

        outcome.Should().BeOfType<ModuleOutcome.Completed>();
        await os.Received().RunArgvCommandAsync(
            Arg.Is<IReadOnlyList<string>>(argv => argv.Any(s => s.Contains("002-next.ps1"))),
            Arg.Any<IReadOnlyDictionary<string, string>>(),

            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Edited_script_with_same_ordinal_re_runs_because_body_hash_differs()
    {
        // Body-hash protection: if the operator edits a script between runs,
        // its checkpoint entry no longer matches and the new body runs.
        var os = Substitute.For<IWindowsOs>();
        os.RunArgvCommandAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(new RunCommandResult(0, "", ""));

        var checkpoint = new InMemoryScriptCheckpointStore();
        var module = CreateModule(checkpoint);
        var ctx = new TestModuleContext(os);

        var original = ResolvedUserData.Empty(new global::Eryph.GuestServices.CloudConfig.CloudConfig())
            with { Scripts = [new ScriptPayload(ScriptKind.PowerShell, Encoding.UTF8.GetBytes("# original body"), "edit.ps1")] };
        await module.ApplyAsync(original, ctx, CancellationToken.None);

        os.ClearReceivedCalls();

        var edited = ResolvedUserData.Empty(new global::Eryph.GuestServices.CloudConfig.CloudConfig())
            with { Scripts = [new ScriptPayload(ScriptKind.PowerShell, Encoding.UTF8.GetBytes("# edited body"), "edit.ps1")] };
        await module.ApplyAsync(edited, ctx, CancellationToken.None);

        // The edited script runs because the body hash differs from the
        // checkpoint entry recorded for ordinal 1. The .ps1 path appears
        // embedded inside the -Command UTF-8 wrapper rather than as a bare
        // -File argv entry, so we substring-match the staged filename.
        await os.Received().RunArgvCommandAsync(
            Arg.Is<IReadOnlyList<string>>(argv => argv.Any(s => s.Contains("001-edit.ps1"))),
            Arg.Any<IReadOnlyDictionary<string, string>>(),

            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Per_script_reboot_quota_fails_after_repeated_1003_without_progress()
    {
        // A broken installer that returns 1003 on every invocation must
        // eventually be failed rather than looped forever. Pin the cap
        // explicitly so the test stays compact regardless of default changes.
        var os = Substitute.For<IWindowsOs>();
        os.RunArgvCommandAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(new RunCommandResult(1003, "", ""));

        var checkpoint = new InMemoryScriptCheckpointStore();
        var settings = new ProvisioningSettings
        {
            Scripts = new ScriptSettings { PerInstanceDirectory = @"C:\temp\eryph-scripts-test" },
            Reboot = new RebootSettings { MaxPerScript = 2 },
        };
        var module = CreateModule(checkpoint, settings);
        var ctx = new TestModuleContext(os);

        var resolved = ResolvedUserData.Empty(new global::Eryph.GuestServices.CloudConfig.CloudConfig())
            with { Scripts = [new ScriptPayload(ScriptKind.PowerShell, Encoding.UTF8.GetBytes("# always 1003"), "stuck.ps1")] };

        // First 1003 marks the script executed (cbi semantics) and returns Reboot.
        // The script will then be skipped on subsequent runs because the
        // (ordinal, body-hash) is in the checkpoint, so the quota check fires
        // through a different path: we re-set the test to make the same
        // script "un-executed" between calls so its 1003 is observable.
        var first = await module.ApplyAsync(resolved, ctx, CancellationToken.None);
        first.Should().BeOfType<ModuleOutcome.RebootRequested>();

        // Force the script back into the not-yet-executed state by clearing
        // the checkpoint's Executed list (RebootCounts persists) — simulates
        // an operator manually clearing the per-script done marker.
        var current = await checkpoint.LoadAsync("test-instance", CancellationToken.None);
        await checkpoint.SaveAsync(
            "test-instance",
            current with { Executed = [] },
            CancellationToken.None);

        var second = await module.ApplyAsync(resolved, ctx, CancellationToken.None);
        second.Should().BeOfType<ModuleOutcome.RebootRequested>();

        await checkpoint.SaveAsync(
            "test-instance",
            (await checkpoint.LoadAsync("test-instance", CancellationToken.None)) with { Executed = [] },
            CancellationToken.None);

        var third = await module.ApplyAsync(resolved, ctx, CancellationToken.None);
        third.Should().BeOfType<ModuleOutcome.Failed>(
            "after MaxRebootsPerScript (=2) the quota fires and the module fails the run");
    }

    [Fact]
    public async Task Per_script_reboot_quota_honors_custom_MaxPerScript_setting()
    {
        // A tighter cap of 1 must fail on the SECOND 1003 (after one reboot),
        // proving the quota reads from ProvisioningSettings.Reboot.MaxPerScript.
        var os = Substitute.For<IWindowsOs>();
        os.RunArgvCommandAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(new RunCommandResult(1003, "", ""));

        var checkpoint = new InMemoryScriptCheckpointStore();
        var settings = new ProvisioningSettings
        {
            Scripts = new ScriptSettings { PerInstanceDirectory = @"C:\temp\eryph-scripts-test" },
            Reboot = new RebootSettings { MaxPerScript = 1 },
        };
        var module = CreateModule(checkpoint, settings);
        var ctx = new TestModuleContext(os);

        var resolved = ResolvedUserData.Empty(new global::Eryph.GuestServices.CloudConfig.CloudConfig())
            with { Scripts = [new ScriptPayload(ScriptKind.PowerShell, Encoding.UTF8.GetBytes("# always 1003"), "stuck.ps1")] };

        var first = await module.ApplyAsync(resolved, ctx, CancellationToken.None);
        first.Should().BeOfType<ModuleOutcome.RebootRequested>();

        await checkpoint.SaveAsync(
            "test-instance",
            (await checkpoint.LoadAsync("test-instance", CancellationToken.None)) with { Executed = [] },
            CancellationToken.None);

        var second = await module.ApplyAsync(resolved, ctx, CancellationToken.None);
        second.Should().BeOfType<ModuleOutcome.Failed>(
            "with MaxPerScript=1 the second 1003 trips the quota");
    }

    [Fact]
    public async Task Script_can_raise_its_own_reboot_limit_via_directive()
    {
        // A user script emits ##egs.reboot_limit=N on stdout to raise its own
        // per-script cap. Parallel to runcmd's directive.
        var os = Substitute.For<IWindowsOs>();
        os.RunArgvCommandAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(new RunCommandResult(1003, "##egs.reboot_limit=5\n", ""));

        var checkpoint = new InMemoryScriptCheckpointStore();
        var settings = new ProvisioningSettings
        {
            Scripts = new ScriptSettings { PerInstanceDirectory = @"C:\temp\eryph-scripts-test" },
            Reboot = new RebootSettings { MaxPerScript = 2 },
        };
        var module = CreateModule(checkpoint, settings);
        var ctx = new TestModuleContext(os);

        var resolved = ResolvedUserData.Empty(new global::Eryph.GuestServices.CloudConfig.CloudConfig())
            with { Scripts = [new ScriptPayload(ScriptKind.PowerShell, Encoding.UTF8.GetBytes("# raise"), "longinstaller.ps1")] };

        // Without the directive a cap of 2 would fail on the third 1003.
        // With the directive raising to 5, the third must still reboot.
        for (var i = 0; i < 3; i++)
        {
            await checkpoint.SaveAsync(
                "test-instance",
                (await checkpoint.LoadAsync("test-instance", CancellationToken.None)) with { Executed = [] },
                CancellationToken.None);
            var outcome = await module.ApplyAsync(resolved, ctx, CancellationToken.None);
            outcome.Should().BeOfType<ModuleOutcome.RebootRequested>(
                "iteration {0}: directive raised limit to 5", i);
        }
    }

    [Fact]
    public async Task Script_receives_EGS_REBOOT_env_vars()
    {
        IReadOnlyDictionary<string, string>? captured = null;
        var os = Substitute.For<IWindowsOs>();
        os.RunArgvCommandAsync(
                Arg.Any<IReadOnlyList<string>>(),
                Arg.Do<IReadOnlyDictionary<string, string>>(env => captured = env),
                Arg.Any<CancellationToken>())
            .Returns(new RunCommandResult(0, "", ""));

        var checkpoint = new InMemoryScriptCheckpointStore();
        var settings = new ProvisioningSettings
        {
            Scripts = new ScriptSettings { PerInstanceDirectory = @"C:\temp\eryph-scripts-test" },
            Reboot = new RebootSettings { MaxPerScript = 7 },
        };
        var module = CreateModule(checkpoint, settings);
        var ctx = new TestModuleContext(os);

        var resolved = ResolvedUserData.Empty(new global::Eryph.GuestServices.CloudConfig.CloudConfig())
            with { Scripts = [new ScriptPayload(ScriptKind.PowerShell, Encoding.UTF8.GetBytes("# envtest"), "envtest.ps1")] };

        await module.ApplyAsync(resolved, ctx, CancellationToken.None);

        captured.Should().NotBeNull();
        captured!["EGS_ENTRY_INDEX"].Should().Be("1");
        captured["EGS_REBOOT_COUNT"].Should().Be("0");
        captured["EGS_REBOOT_LIMIT"].Should().Be("7");
    }
}
