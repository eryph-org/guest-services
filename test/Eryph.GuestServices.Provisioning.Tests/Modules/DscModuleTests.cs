using AwesomeAssertions;
using Eryph.GuestServices.CloudConfig;
using Eryph.GuestServices.Provisioning.Modules;
using Eryph.GuestServices.Provisioning.UserData;
using Eryph.GuestServices.Provisioning.Windows;
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
}
