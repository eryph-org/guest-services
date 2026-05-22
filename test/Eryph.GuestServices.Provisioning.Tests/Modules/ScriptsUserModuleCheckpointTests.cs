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

    private static ScriptsUserModule CreateModule(IScriptCheckpointStore checkpointStore) =>
        new(NullLogger<ScriptsUserModule>.Instance,
            TestSettings,
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
            Arg.Is<IReadOnlyList<string>>(argv => argv.Any(s => s.EndsWith("003-three.ps1"))),
            Arg.Any<CancellationToken>()).Returns(new RunCommandResult(1003, "", ""));
        os.RunArgvCommandAsync(
            Arg.Is<IReadOnlyList<string>>(argv => argv.Any(s => s.EndsWith("001-one.ps1") || s.EndsWith("002-two.ps1") || s.EndsWith("004-four.ps1"))),
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
            Arg.Is<IReadOnlyList<string>>(argv => argv.Any(s => s.EndsWith("001-one.ps1"))),
            Arg.Any<CancellationToken>());
        await os.Received().RunArgvCommandAsync(
            Arg.Is<IReadOnlyList<string>>(argv => argv.Any(s => s.EndsWith("003-three.ps1"))),
            Arg.Any<CancellationToken>());
        await os.DidNotReceive().RunArgvCommandAsync(
            Arg.Is<IReadOnlyList<string>>(argv => argv.Any(s => s.EndsWith("004-four.ps1"))),
            Arg.Any<CancellationToken>());

        // Round 2 (post-reboot resume): scripts 1, 2, 3 are checkpointed →
        // skipped. Script 4 executes for the first time.
        os.ClearReceivedCalls();
        // Round-2 calls all return 0 (Hyper-V finished installing across the reboot).
        os.RunArgvCommandAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
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
            Arg.Is<IReadOnlyList<string>>(argv => argv.Any(s => s.EndsWith("001-one.ps1")
                                                                || s.EndsWith("002-two.ps1")
                                                                || s.EndsWith("003-three.ps1"))),
            Arg.Any<CancellationToken>());
        // Script 4 must run.
        await os.Received().RunArgvCommandAsync(
            Arg.Is<IReadOnlyList<string>>(argv => argv.Any(s => s.EndsWith("004-four.ps1"))),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Edited_script_with_same_ordinal_re_runs_because_body_hash_differs()
    {
        // Body-hash protection: if the operator edits a script between runs,
        // its checkpoint entry no longer matches and the new body runs.
        var os = Substitute.For<IWindowsOs>();
        os.RunArgvCommandAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
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
        // checkpoint entry recorded for ordinal 1.
        await os.Received().RunArgvCommandAsync(
            Arg.Is<IReadOnlyList<string>>(argv => argv.Any(s => s.EndsWith("001-edit.ps1"))),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Per_script_reboot_quota_fails_after_repeated_1003_without_progress()
    {
        // A broken installer that returns 1003 on every invocation must
        // eventually be failed rather than looped forever (MaxRebootsPerScript = 2).
        var os = Substitute.For<IWindowsOs>();
        os.RunArgvCommandAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new RunCommandResult(1003, "", ""));

        var checkpoint = new InMemoryScriptCheckpointStore();
        var module = CreateModule(checkpoint);
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
}
