using AwesomeAssertions;
using Eryph.GuestServices.CloudConfig;
using Eryph.GuestServices.Provisioning.Configuration;
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
        os.RunShellCommandAsync(Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(new RunCommandResult(0, "", ""));

        var module = CreateModule();
        var userData = ResolvedUserData.Empty(new CloudConfigModel
        {
            Runcmd =
            [
                new RuncmdEntry { IsShellCommand = true, Command = "echo hi" },
            ],
        });

        var result = await module.ApplyAsync(userData, new TestModuleContext(os), CancellationToken.None);

        result.Should().BeOfType<ModuleOutcome.Completed>();
        await os.Received().RunShellCommandAsync("echo hi", Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Runs_argv_commands_through_RunArgvCommandAsync()
    {
        var os = Substitute.For<IWindowsOs>();
        os.RunArgvCommandAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(new RunCommandResult(0, "", ""));

        var module = CreateModule();
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
            Arg.Any<IReadOnlyDictionary<string, string>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Returns_RebootRequested_when_command_exits_1003_and_re_entry_runs_same_entry_again()
    {
        // 1003 means "I'm not done; reboot, re-run me." The checkpoint must
        // NOT mark the entry completed so a resume runs it again.
        var os = Substitute.For<IWindowsOs>();
        os.RunShellCommandAsync("first", Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(new RunCommandResult(RuncmdModule.RebootAndContinueExitCode, "", ""));

        var checkpointStore = new InMemoryRuncmdCheckpointStore();
        var module = CreateModule(checkpointStore: checkpointStore);
        var userData = ResolvedUserData.Empty(new CloudConfigModel
        {
            Runcmd =
            [
                new RuncmdEntry { IsShellCommand = true, Command = "first" },
                new RuncmdEntry { IsShellCommand = true, Command = "second" },
            ],
        });

        var first = await module.ApplyAsync(userData, new TestModuleContext(os), CancellationToken.None);
        var reboot = first.Should().BeOfType<ModuleOutcome.RebootRequested>().Subject;
        reboot.IsScriptDriven.Should().BeTrue("1003 reboots are script-driven and bypass the module-wide cap");
        await os.DidNotReceive().RunShellCommandAsync("second", Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<CancellationToken>());

        // Resume: 'first' must run AGAIN (not be skipped). Stub it to succeed
        // this time so the queue can finish.
        os.ClearReceivedCalls();
        os.RunShellCommandAsync("first", Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(new RunCommandResult(0, "", ""));
        os.RunShellCommandAsync("second", Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(new RunCommandResult(0, "", ""));

        var second = await module.ApplyAsync(userData, new TestModuleContext(os), CancellationToken.None);

        second.Should().BeOfType<ModuleOutcome.Completed>();
        await os.Received(1).RunShellCommandAsync("first", Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<CancellationToken>());
        await os.Received(1).RunShellCommandAsync("second", Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Returns_RebootRequested_when_command_exits_1001_and_re_entry_skips_the_entry()
    {
        // 1001 means "reboot, but this entry is done." Resume must skip it.
        var os = Substitute.For<IWindowsOs>();
        os.RunShellCommandAsync("first", Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(new RunCommandResult(RuncmdModule.RebootAndDoneExitCode, "", ""));
        os.RunShellCommandAsync("second", Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(new RunCommandResult(0, "", ""));

        var checkpointStore = new InMemoryRuncmdCheckpointStore();
        var module = CreateModule(checkpointStore: checkpointStore);
        var userData = ResolvedUserData.Empty(new CloudConfigModel
        {
            Runcmd =
            [
                new RuncmdEntry { IsShellCommand = true, Command = "first" },
                new RuncmdEntry { IsShellCommand = true, Command = "second" },
            ],
        });

        var first = await module.ApplyAsync(userData, new TestModuleContext(os), CancellationToken.None);
        var reboot = first.Should().BeOfType<ModuleOutcome.RebootRequested>().Subject;
        reboot.IsScriptDriven.Should().BeTrue();

        os.ClearReceivedCalls();
        var second = await module.ApplyAsync(userData, new TestModuleContext(os), CancellationToken.None);

        second.Should().BeOfType<ModuleOutcome.Completed>();
        // 'first' is completed; only 'second' runs on resume.
        await os.DidNotReceive().RunShellCommandAsync("first", Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<CancellationToken>());
        await os.Received(1).RunShellCommandAsync("second", Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Returns_Fail_when_an_entry_exceeds_its_per_entry_reboot_limit()
    {
        var os = Substitute.For<IWindowsOs>();
        os.RunShellCommandAsync(Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(new RunCommandResult(RuncmdModule.RebootAndContinueExitCode, "", ""));

        var checkpointStore = new InMemoryRuncmdCheckpointStore();
        var settings = new ProvisioningSettings { Runcmd = new RuncmdSettings { MaxRebootsPerEntry = 2 } };
        var module = CreateModule(settings, checkpointStore);
        var userData = ResolvedUserData.Empty(new CloudConfigModel
        {
            Runcmd = [new RuncmdEntry { IsShellCommand = true, Command = "loops" }],
        });

        // Limit = 2 → 2 reboot requests allowed; the 3rd attempt must Fail.
        (await module.ApplyAsync(userData, new TestModuleContext(os), CancellationToken.None))
            .Should().BeOfType<ModuleOutcome.RebootRequested>();
        (await module.ApplyAsync(userData, new TestModuleContext(os), CancellationToken.None))
            .Should().BeOfType<ModuleOutcome.RebootRequested>();
        var third = await module.ApplyAsync(userData, new TestModuleContext(os), CancellationToken.None);

        var failed = third.Should().BeOfType<ModuleOutcome.Failed>().Subject;
        failed.Reason.Should().Contain("exceeded per-entry reboot limit");
    }

    [Fact]
    public async Task Honors_script_emitted_EGS_RUNCMD_REBOOT_LIMIT_override()
    {
        // First attempt returns 1003 with stdout `EGS_RUNCMD_REBOOT_LIMIT=5`,
        // bumping the limit past the configured default (2). The 3rd attempt
        // — which would have failed at the default — succeeds.
        var os = Substitute.For<IWindowsOs>();
        os.RunShellCommandAsync(Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(new RunCommandResult(RuncmdModule.RebootAndContinueExitCode, "EGS_RUNCMD_REBOOT_LIMIT=5\n", ""));

        var checkpointStore = new InMemoryRuncmdCheckpointStore();
        var settings = new ProvisioningSettings { Runcmd = new RuncmdSettings { MaxRebootsPerEntry = 2 } };
        var module = CreateModule(settings, checkpointStore);
        var userData = ResolvedUserData.Empty(new CloudConfigModel
        {
            Runcmd = [new RuncmdEntry { IsShellCommand = true, Command = "longinstaller" }],
        });

        // With the override in place, 3 consecutive 1003s must all return Reboot
        // (would Fail at the configured default of 2).
        for (var i = 0; i < 3; i++)
        {
            var outcome = await module.ApplyAsync(userData, new TestModuleContext(os), CancellationToken.None);
            outcome.Should().BeOfType<ModuleOutcome.RebootRequested>(
                "iteration {0}: the script raised the limit to 5", i);
        }
    }

    [Fact]
    public async Task Override_is_persisted_so_the_script_does_not_need_to_re_emit()
    {
        // The marker is emitted only on the FIRST reboot iteration. Subsequent
        // iterations don't print it, but the override survives because we
        // persisted it in the checkpoint.
        var os = Substitute.For<IWindowsOs>();
        os.RunShellCommandAsync(Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(
                new RunCommandResult(RuncmdModule.RebootAndContinueExitCode, "EGS_RUNCMD_REBOOT_LIMIT=4\n", ""),
                new RunCommandResult(RuncmdModule.RebootAndContinueExitCode, "", ""),
                new RunCommandResult(RuncmdModule.RebootAndContinueExitCode, "", ""),
                new RunCommandResult(RuncmdModule.RebootAndContinueExitCode, "", ""));

        var checkpointStore = new InMemoryRuncmdCheckpointStore();
        var settings = new ProvisioningSettings { Runcmd = new RuncmdSettings { MaxRebootsPerEntry = 2 } };
        var module = CreateModule(settings, checkpointStore);
        var userData = ResolvedUserData.Empty(new CloudConfigModel
        {
            Runcmd = [new RuncmdEntry { IsShellCommand = true, Command = "longinstaller" }],
        });

        for (var i = 0; i < 4; i++)
        {
            var outcome = await module.ApplyAsync(userData, new TestModuleContext(os), CancellationToken.None);
            outcome.Should().BeOfType<ModuleOutcome.RebootRequested>(
                "iteration {0}: limit of 4 survives across reboots", i);
        }
    }

    [Fact]
    public async Task Injects_EGS_RUNCMD_env_vars_with_current_reboot_count_and_limit()
    {
        var os = Substitute.For<IWindowsOs>();
        IReadOnlyDictionary<string, string>? captured = null;
        os.RunShellCommandAsync(Arg.Any<string>(),
                Arg.Do<IReadOnlyDictionary<string, string>>(env => captured = env),
                Arg.Any<CancellationToken>())
            .Returns(new RunCommandResult(RuncmdModule.RebootAndContinueExitCode, "", ""));

        var checkpointStore = new InMemoryRuncmdCheckpointStore();
        var settings = new ProvisioningSettings { Runcmd = new RuncmdSettings { MaxRebootsPerEntry = 7 } };
        var module = CreateModule(settings, checkpointStore);
        var userData = ResolvedUserData.Empty(new CloudConfigModel
        {
            Runcmd = [new RuncmdEntry { IsShellCommand = true, Command = "step" }],
        });

        await module.ApplyAsync(userData, new TestModuleContext(os), CancellationToken.None);

        captured.Should().NotBeNull();
        captured!["EGS_RUNCMD_ENTRY_INDEX"].Should().Be("1");
        captured["EGS_RUNCMD_REBOOT_COUNT"].Should().Be("0");
        captured["EGS_RUNCMD_REBOOT_LIMIT"].Should().Be("7");

        // After one reboot, the count must reflect the persisted attempt.
        captured = null;
        await module.ApplyAsync(userData, new TestModuleContext(os), CancellationToken.None);
        captured!["EGS_RUNCMD_REBOOT_COUNT"].Should().Be("1");
    }

    [Fact]
    public async Task Treats_1002_as_unsupported_and_continues_with_next_entry()
    {
        var os = Substitute.For<IWindowsOs>();
        os.RunShellCommandAsync("first", Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(new RunCommandResult(RuncmdModule.RerunOnNextBootExitCode, "", ""));
        os.RunShellCommandAsync("second", Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(new RunCommandResult(0, "", ""));

        var module = CreateModule();
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
        await os.Received(1).RunShellCommandAsync("second", Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Continues_after_non_zero_non_reboot_exit_code()
    {
        var os = Substitute.For<IWindowsOs>();
        os.RunShellCommandAsync("first", Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(new RunCommandResult(2, "", ""));
        os.RunShellCommandAsync("second", Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(new RunCommandResult(0, "", ""));

        var module = CreateModule();
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
        await os.Received().RunShellCommandAsync("second", Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Returns_completed_when_runcmd_is_empty()
    {
        var os = Substitute.For<IWindowsOs>();
        var module = CreateModule();
        var userData = ResolvedUserData.Empty(new CloudConfigModel());

        var result = await module.ApplyAsync(userData, new TestModuleContext(os), CancellationToken.None);

        result.Should().BeOfType<ModuleOutcome.Completed>();
    }

    [Fact]
    public async Task Editing_a_runcmd_entry_invalidates_its_completed_marker()
    {
        // If the operator edits an entry between runs (ordinal unchanged,
        // content changed), the checkpoint must NOT skip the new content.
        var os = Substitute.For<IWindowsOs>();
        os.RunShellCommandAsync(Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(new RunCommandResult(0, "", ""));

        var checkpointStore = new InMemoryRuncmdCheckpointStore();
        var module = CreateModule(checkpointStore: checkpointStore);

        await module.ApplyAsync(
            ResolvedUserData.Empty(new CloudConfigModel
            {
                Runcmd = [new RuncmdEntry { IsShellCommand = true, Command = "version-1" }],
            }),
            new TestModuleContext(os),
            CancellationToken.None);

        os.ClearReceivedCalls();
        await module.ApplyAsync(
            ResolvedUserData.Empty(new CloudConfigModel
            {
                Runcmd = [new RuncmdEntry { IsShellCommand = true, Command = "version-2" }],
            }),
            new TestModuleContext(os),
            CancellationToken.None);

        await os.Received(1).RunShellCommandAsync("version-2", Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<CancellationToken>());
    }

    private static RuncmdModule CreateModule(
        ProvisioningSettings? settings = null,
        IRuncmdCheckpointStore? checkpointStore = null) =>
        new(
            NullLogger<RuncmdModule>.Instance,
            settings ?? new ProvisioningSettings(),
            checkpointStore ?? new InMemoryRuncmdCheckpointStore());

    private sealed class InMemoryRuncmdCheckpointStore : IRuncmdCheckpointStore
    {
        private readonly Dictionary<string, RuncmdCheckpoint> _byInstance = new(StringComparer.Ordinal);

        public Task<RuncmdCheckpoint> LoadAsync(string instanceId, CancellationToken cancellationToken) =>
            Task.FromResult(_byInstance.GetValueOrDefault(instanceId) ?? RuncmdCheckpoint.Empty);

        public Task SaveAsync(string instanceId, RuncmdCheckpoint checkpoint, CancellationToken cancellationToken)
        {
            _byInstance[instanceId] = checkpoint;
            return Task.CompletedTask;
        }

        public Task ResetAsync(string instanceId, CancellationToken cancellationToken)
        {
            _byInstance.Remove(instanceId);
            return Task.CompletedTask;
        }
    }
}
