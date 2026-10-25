using AwesomeAssertions;
using Eryph.GuestServices.CloudConfig;
using Eryph.GuestServices.Provisioning.Modules;
using Eryph.GuestServices.Provisioning.UserData;
using Eryph.GuestServices.Provisioning.Windows;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using CloudConfigModel = global::Eryph.GuestServices.CloudConfig.CloudConfig;

namespace Eryph.GuestServices.Provisioning.Tests.Modules;

public sealed class DscModuleTests
{
    private const string DscPath = @"C:\Program Files\dsc\dsc.exe";

    private static DscModule CreateModule() => new(NullLogger<DscModule>.Instance);

    private static IWindowsOs DscOs(
        string? dscPath = DscPath,
        bool pwsh = true,
        int runExitCode = 0,
        bool reboot = false)
    {
        var os = Substitute.For<IWindowsOs>();
        os.ResolveDscExecutableAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(dscPath);
        os.IsPowerShell7AvailableAsync(Arg.Any<CancellationToken>()).Returns(pwsh);
        os.WriteDscDocumentAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => @"C:\ProgramData\eryph\dsc\" + ci.ArgAt<string>(0));
        os.DownloadDscDocumentAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ci => @"C:\ProgramData\eryph\dsc\" + ci.ArgAt<string>(0));
        os.TranslateUnixPath(Arg.Any<string>()).Returns(ci => ci.ArgAt<string>(0));
        os.RunDscConfigAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new DscApplyResult { ExitCode = runExitCode, RebootRequested = reboot });
        return os;
    }

    private static DscConfiguration Inline(string name = "baseline") =>
        new() { Name = name, Document = "$schema: x\nresources: []\n" };

    private static Task<ModuleOutcome> Apply(DscModule module, IWindowsOs os, CloudConfigModel config) =>
        module.ApplyAsync(ResolvedUserData.Empty(config), new TestModuleContext(os), CancellationToken.None);

    [Fact]
    public async Task No_dsc_block_is_a_noop()
    {
        var os = Substitute.For<IWindowsOs>();

        var outcome = await Apply(CreateModule(), os, new CloudConfigModel());

        outcome.Should().BeOfType<ModuleOutcome.Completed>();
        await os.DidNotReceive().ResolveDscExecutableAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Empty_block_with_no_configurations_is_a_noop()
    {
        var os = Substitute.For<IWindowsOs>();
        var config = new CloudConfigModel { Dsc = new DscConfig() };

        var outcome = await Apply(CreateModule(), os, config);

        outcome.Should().BeOfType<ModuleOutcome.Completed>();
        await os.DidNotReceive().ResolveDscExecutableAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Applies_each_configuration_with_set_verb_when_dsc_present()
    {
        var os = DscOs();
        var config = new CloudConfigModel
        {
            Dsc = new DscConfig { Configurations = [Inline("one"), Inline("two")] },
        };

        var outcome = await Apply(CreateModule(), os, config);

        outcome.Should().BeOfType<ModuleOutcome.Completed>();
        // Default verb is 'set'; the inline document is staged then applied.
        await os.Received(2).WriteDscDocumentAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await os.Received(2).RunDscConfigAsync(DscPath, Arg.Any<string>(), "set", Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("test")]
    [InlineData("get")]
    [InlineData("SET")]
    public async Task Apply_verb_is_normalized_and_passed_through(string apply)
    {
        var os = DscOs();
        var config = new CloudConfigModel
        {
            Dsc = new DscConfig { Apply = apply, Configurations = [Inline()] },
        };

        var outcome = await Apply(CreateModule(), os, config);

        outcome.Should().BeOfType<ModuleOutcome.Completed>();
        await os.Received().RunDscConfigAsync(
            DscPath, Arg.Any<string>(), apply.ToLowerInvariant(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Invalid_apply_verb_fails_without_running()
    {
        var os = DscOs();
        var config = new CloudConfigModel
        {
            Dsc = new DscConfig { Apply = "apply", Configurations = [Inline()] },
        };

        var outcome = await Apply(CreateModule(), os, config);

        outcome.Should().BeOfType<ModuleOutcome.Failed>();
        await os.DidNotReceive().RunDscConfigAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Installs_via_winget_when_dsc_missing_and_install_defaulted()
    {
        var os = DscOs(dscPath: null);
        // Not installed on the first probe; present after the winget install.
        os.ResolveDscExecutableAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns((string?)null, DscPath);
        os.RunWingetAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new RunCommandResult(0, "", ""));
        var config = new CloudConfigModel { Dsc = new DscConfig { Configurations = [Inline()] } };

        var outcome = await Apply(CreateModule(), os, config);

        outcome.Should().BeOfType<ModuleOutcome.Completed>();
        await os.Received().EnsureWingetWrapperAsync(Arg.Any<CancellationToken>());
        await os.Received().RunWingetAsync(
            Arg.Is<IReadOnlyList<string>>(a => a.Contains("install") && a.Contains("Microsoft.DSC")),
            Arg.Any<CancellationToken>());
        await os.Received().RunDscConfigAsync(DscPath, Arg.Any<string>(), "set", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Install_skip_and_missing_fails_fast()
    {
        var os = DscOs(dscPath: null);
        var config = new CloudConfigModel
        {
            Dsc = new DscConfig { Install = "skip", Configurations = [Inline()] },
        };

        var outcome = await Apply(CreateModule(), os, config);

        outcome.Should().BeOfType<ModuleOutcome.Failed>();
        await os.DidNotReceive().RunWingetAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
        await os.DidNotReceive().InstallDscFromGithubReleaseAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Install_source_github_release_downloads_the_asset()
    {
        var os = DscOs(dscPath: null);
        os.ResolveDscExecutableAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns((string?)null, DscPath);
        var config = new CloudConfigModel
        {
            Dsc = new DscConfig
            {
                InstallSource = "github_release",
                InstallVersion = "3.1.0",
                Configurations = [Inline()],
            },
        };

        var outcome = await Apply(CreateModule(), os, config);

        outcome.Should().BeOfType<ModuleOutcome.Completed>();
        await os.Received().InstallDscFromGithubReleaseAsync("3.1.0", Arg.Any<CancellationToken>());
        await os.DidNotReceive().RunWingetAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Install_source_path_but_unresolvable_fails()
    {
        // ResolveDscExecutableAsync returns null for a missing / untrusted path.
        var os = DscOs(dscPath: null);
        var config = new CloudConfigModel
        {
            Dsc = new DscConfig
            {
                InstallSource = "path",
                Path = @"C:\tools\dsc.exe",
                Configurations = [Inline()],
            },
        };

        var outcome = await Apply(CreateModule(), os, config);

        outcome.Should().BeOfType<ModuleOutcome.Failed>();
        await os.DidNotReceive().RunWingetAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
        await os.DidNotReceive().InstallDscFromGithubReleaseAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Winget_install_failure_returns_Fail()
    {
        var os = DscOs(dscPath: null);
        os.RunWingetAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new RunCommandResult(1, "", ""));
        var config = new CloudConfigModel { Dsc = new DscConfig { Configurations = [Inline()] } };

        var outcome = await Apply(CreateModule(), os, config);

        outcome.Should().BeOfType<ModuleOutcome.Failed>();
        await os.DidNotReceive().RunDscConfigAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Configuration_failure_stops_at_first_when_continue_on_error_false()
    {
        var os = DscOs(runExitCode: 2);
        var config = new CloudConfigModel
        {
            Dsc = new DscConfig { Configurations = [Inline("one"), Inline("two")] },
        };

        var outcome = await Apply(CreateModule(), os, config);

        outcome.Should().BeOfType<ModuleOutcome.Failed>();
        // Stopped after the first failing configuration.
        await os.Received(1).RunDscConfigAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Continue_on_error_applies_all_then_fails()
    {
        var os = DscOs(runExitCode: 2);
        var config = new CloudConfigModel
        {
            Dsc = new DscConfig
            {
                ContinueOnError = true,
                Configurations = [Inline("one"), Inline("two")],
            },
        };

        var outcome = await Apply(CreateModule(), os, config);

        outcome.Should().BeOfType<ModuleOutcome.Failed>();
        await os.Received(2).RunDscConfigAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Document_download_timeout_honours_continue_on_error()
    {
        var os = DscOs();
        // The first document download times out — its internal timeout surfaces
        // as OperationCanceledException while the provisioning token is still
        // live. That must be a per-configuration failure, not a module abort, so
        // the second configuration still runs.
        var call = 0;
        os.DownloadDscDocumentAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns<string>(ci =>
            {
                if (Interlocked.Increment(ref call) == 1)
                    throw new OperationCanceledException();
                return @"C:\ProgramData\eryph\dsc\" + ci.ArgAt<string>(0);
            });
        var config = new CloudConfigModel
        {
            Dsc = new DscConfig
            {
                ContinueOnError = true,
                Configurations =
                [
                    new DscConfiguration { Name = "one", DocumentUrl = "https://example.invalid/a.yaml" },
                    new DscConfiguration { Name = "two", DocumentUrl = "https://example.invalid/b.yaml" },
                ],
            },
        };

        var outcome = await Apply(CreateModule(), os, config);

        outcome.Should().BeOfType<ModuleOutcome.Failed>();
        // The second configuration was staged and applied despite the first timing out.
        await os.Received(1).RunDscConfigAsync(DscPath, Arg.Any<string>(), "set", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Genuine_cancellation_during_staging_aborts_the_module()
    {
        var os = DscOs();
        using var cts = new CancellationTokenSource();
        os.DownloadDscDocumentAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns<string>(_ =>
            {
                cts.Cancel();
                throw new OperationCanceledException(cts.Token);
            });
        var config = new CloudConfigModel
        {
            Dsc = new DscConfig
            {
                ContinueOnError = true,
                Configurations =
                [
                    new DscConfiguration { Name = "one", DocumentUrl = "https://example.invalid/a.yaml" },
                    new DscConfiguration { Name = "two", DocumentUrl = "https://example.invalid/b.yaml" },
                ],
            },
        };

        var act = () => CreateModule().ApplyAsync(
            ResolvedUserData.Empty(config), new TestModuleContext(os), cts.Token);

        // A real provisioning cancel propagates — it is not swallowed by continue_on_error.
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Apply_exception_honours_continue_on_error()
    {
        var os = DscOs();
        // RunDscConfigAsync THROWS for the first configuration (as it does for an
        // untrusted document_path) rather than returning a non-zero exit. That
        // must be a per-configuration failure, not an unhandled exception that
        // aborts provisioning — the second configuration still runs.
        var call = 0;
        os.RunDscConfigAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<DscApplyResult>(_ =>
            {
                if (Interlocked.Increment(ref call) == 1)
                    throw new InvalidOperationException("Refusing to apply a DSC document from an untrusted path.");
                return new DscApplyResult { ExitCode = 0 };
            });
        var config = new CloudConfigModel
        {
            Dsc = new DscConfig { ContinueOnError = true, Configurations = [Inline("one"), Inline("two")] },
        };

        var outcome = await Apply(CreateModule(), os, config);

        outcome.Should().BeOfType<ModuleOutcome.Failed>();
        await os.Received(2).RunDscConfigAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Apply_exception_stops_at_first_when_continue_on_error_false()
    {
        var os = DscOs();
        os.RunDscConfigAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<DscApplyResult>(_ => throw new InvalidOperationException("untrusted path"));
        var config = new CloudConfigModel
        {
            Dsc = new DscConfig { Configurations = [Inline("one"), Inline("two")] },
        };

        var outcome = await Apply(CreateModule(), os, config);

        outcome.Should().BeOfType<ModuleOutcome.Failed>();
        await os.Received(1).RunDscConfigAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Apply_genuine_cancellation_propagates()
    {
        var os = DscOs();
        using var cts = new CancellationTokenSource();
        os.RunDscConfigAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<DscApplyResult>(_ =>
            {
                cts.Cancel();
                throw new OperationCanceledException(cts.Token);
            });
        var config = new CloudConfigModel
        {
            Dsc = new DscConfig { ContinueOnError = true, Configurations = [Inline("one"), Inline("two")] },
        };

        var act = () => CreateModule().ApplyAsync(
            ResolvedUserData.Empty(config), new TestModuleContext(os), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Test_verb_surfaces_the_result_output_on_the_log()
    {
        var logger = new CapturingLogger<DscModule>();
        var os = DscOs();
        os.RunDscConfigAsync(Arg.Any<string>(), Arg.Any<string>(), "test", Arg.Any<CancellationToken>())
            .Returns(new DscApplyResult { ExitCode = 0, JsonOutput = "{ \"inDesiredState\": false }" });
        var config = new CloudConfigModel
        {
            Dsc = new DscConfig { Apply = "test", Configurations = [Inline()] },
        };

        var outcome = await new DscModule(logger).ApplyAsync(
            ResolvedUserData.Empty(config), new TestModuleContext(os), CancellationToken.None);

        outcome.Should().BeOfType<ModuleOutcome.Completed>();
        // The read-only verb's output is surfaced, not discarded.
        logger.Messages.Should().Contain(m => m.Contains("inDesiredState"));
    }

    [Fact]
    public async Task Test_verb_does_not_reboot_even_when_a_restart_is_reported()
    {
        // A 'test'/'get' run can surface restartRequired as reported state;
        // inspecting a guest must never reboot it. Only 'set' acts on a reboot.
        var os = DscOs(reboot: true);
        var config = new CloudConfigModel
        {
            Dsc = new DscConfig { Apply = "test", Configurations = [Inline()] },
        };

        var outcome = await Apply(CreateModule(), os, config);

        outcome.Should().BeOfType<ModuleOutcome.Completed>();
    }

    [Fact]
    public async Task Winget_install_failure_surfaces_output_in_the_reason()
    {
        var os = DscOs(dscPath: null);
        os.RunWingetAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new RunCommandResult(1, "no applicable installer found", "winget: install failed"));
        var config = new CloudConfigModel { Dsc = new DscConfig { Configurations = [Inline()] } };

        var outcome = await Apply(CreateModule(), os, config);

        outcome.Should().BeOfType<ModuleOutcome.Failed>()
            .Which.Reason.Should().Contain("winget: install failed").And.Contain("no applicable installer found");
    }

    [Fact]
    public async Task Failed_dsc_run_logs_bounded_stderr_diagnostics()
    {
        var logger = new CapturingLogger<DscModule>();
        var os = DscOs();
        os.RunDscConfigAsync(Arg.Any<string>(), Arg.Any<string>(), "set", Arg.Any<CancellationToken>())
            .Returns(new DscApplyResult { ExitCode = 2, StdErr = "resource 'Foo' not found", JsonOutput = "" });
        var config = new CloudConfigModel { Dsc = new DscConfig { Configurations = [Inline()] } };

        var outcome = await new DscModule(logger).ApplyAsync(
            ResolvedUserData.Empty(config), new TestModuleContext(os), CancellationToken.None);

        outcome.Should().BeOfType<ModuleOutcome.Failed>();
        logger.Messages.Should().Contain(m => m.Contains("resource 'Foo' not found"));
    }

    [Fact]
    public async Task Reboot_request_surfaces_as_Reboot_when_all_succeed()
    {
        var os = DscOs(reboot: true);
        var config = new CloudConfigModel { Dsc = new DscConfig { Configurations = [Inline()] } };

        var outcome = await Apply(CreateModule(), os, config);

        outcome.Should().BeOfType<ModuleOutcome.RebootRequested>();
    }

    [Fact]
    public async Task Reboot_request_is_suppressed_when_another_configuration_fails()
    {
        var os = DscOs();
        // First configuration asks for a reboot; the second fails.
        os.RunDscConfigAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(
                new DscApplyResult { ExitCode = 0, RebootRequested = true },
                new DscApplyResult { ExitCode = 2, RebootRequested = false });
        var config = new CloudConfigModel
        {
            Dsc = new DscConfig
            {
                ContinueOnError = true,
                Configurations = [Inline("one"), Inline("two")],
            },
        };

        var outcome = await Apply(CreateModule(), os, config);

        outcome.Should().BeOfType<ModuleOutcome.Failed>();
    }

    [Fact]
    public async Task Document_url_is_downloaded_with_its_checksum()
    {
        var os = DscOs();
        var config = new CloudConfigModel
        {
            Dsc = new DscConfig
            {
                Configurations =
                [
                    new DscConfiguration
                    {
                        Name = "from-url",
                        DocumentUrl = "https://example.invalid/baseline.dsc.yaml",
                        Checksum = "sha256:abc",
                    },
                ],
            },
        };

        var outcome = await Apply(CreateModule(), os, config);

        outcome.Should().BeOfType<ModuleOutcome.Completed>();
        await os.Received().DownloadDscDocumentAsync(
            Arg.Any<string>(), "https://example.invalid/baseline.dsc.yaml", "sha256:abc", Arg.Any<CancellationToken>());
        await os.DidNotReceive().WriteDscDocumentAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Document_path_is_translated_and_applied()
    {
        var os = DscOs();
        var config = new CloudConfigModel
        {
            Dsc = new DscConfig
            {
                Configurations =
                [
                    new DscConfiguration { Name = "from-file", DocumentPath = "/var/lib/eryph/baseline.dsc.yaml" },
                ],
            },
        };

        var outcome = await Apply(CreateModule(), os, config);

        outcome.Should().BeOfType<ModuleOutcome.Completed>();
        os.Received().TranslateUnixPath("/var/lib/eryph/baseline.dsc.yaml");
        await os.DidNotReceive().WriteDscDocumentAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await os.DidNotReceive().DownloadDscDocumentAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Configuration_with_no_document_source_fails()
    {
        var os = DscOs();
        var config = new CloudConfigModel
        {
            Dsc = new DscConfig { Configurations = [new DscConfiguration { Name = "empty" }] },
        };

        var outcome = await Apply(CreateModule(), os, config);

        outcome.Should().BeOfType<ModuleOutcome.Failed>();
        await os.DidNotReceive().RunDscConfigAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Configuration_with_two_document_sources_fails()
    {
        var os = DscOs();
        var config = new CloudConfigModel
        {
            Dsc = new DscConfig
            {
                Configurations =
                [
                    new DscConfiguration
                    {
                        Name = "ambiguous",
                        Document = "$schema: x",
                        DocumentPath = "/var/baseline.yaml",
                    },
                ],
            },
        };

        var outcome = await Apply(CreateModule(), os, config);

        outcome.Should().BeOfType<ModuleOutcome.Failed>();
    }

    [Fact]
    public async Task Missing_pwsh_warns_but_still_succeeds()
    {
        var os = DscOs(pwsh: false);
        var config = new CloudConfigModel { Dsc = new DscConfig { Configurations = [Inline()] } };

        var outcome = await Apply(CreateModule(), os, config);

        outcome.Should().BeOfType<ModuleOutcome.Completed>();
        await os.Received().RunDscConfigAsync(DscPath, Arg.Any<string>(), "set", Arg.Any<CancellationToken>());
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
