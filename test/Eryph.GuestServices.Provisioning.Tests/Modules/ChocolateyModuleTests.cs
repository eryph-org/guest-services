using AwesomeAssertions;
using Eryph.GuestServices.CloudConfig;
using Eryph.GuestServices.Provisioning.Modules;
using Eryph.GuestServices.Provisioning.UserData;
using Eryph.GuestServices.Provisioning.Windows;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using CloudConfigModel = global::Eryph.GuestServices.CloudConfig.CloudConfig;

namespace Eryph.GuestServices.Provisioning.Tests.Modules;

public sealed class ChocolateyModuleTests
{
    private static ChocolateyModule CreateModule() => new(NullLogger<ChocolateyModule>.Instance);

    private static IWindowsOs ChocoOs(bool installed, int installExitCode = 0, bool bootstrapReboot = false)
    {
        var os = Substitute.For<IWindowsOs>();
        os.IsChocolateyInstalledAsync(Arg.Any<CancellationToken>()).Returns(installed);
        os.BootstrapChocolateyAsync(Arg.Any<CancellationToken>())
            .Returns(new ChocolateyBootstrapResult { RebootRequired = bootstrapReboot });
        os.RunChocoAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new RunCommandResult(installExitCode, "", ""));
        return os;
    }

    private static Task<ModuleOutcome> Apply(ChocolateyModule module, IWindowsOs os, CloudConfigModel config) =>
        module.ApplyAsync(ResolvedUserData.Empty(config), new TestModuleContext(os), CancellationToken.None);

    private static CloudConfigModel WithPackages(params string[] names) => new()
    {
        Chocolatey = new ChocolateyConfig
        {
            Packages = [.. names.Select(n => new ChocolateyPackage { Name = n })],
        },
    };

    [Fact]
    public async Task No_chocolatey_block_is_a_noop()
    {
        var os = Substitute.For<IWindowsOs>();

        var outcome = await Apply(CreateModule(), os, new CloudConfigModel());

        outcome.Should().BeOfType<ModuleOutcome.Completed>();
        await os.DidNotReceive().IsChocolateyInstalledAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Bootstraps_when_missing_and_install_defaulted()
    {
        var os = ChocoOs(installed: false);

        var outcome = await Apply(CreateModule(), os, WithPackages("7zip"));

        outcome.Should().BeOfType<ModuleOutcome.Completed>();
        await os.Received().BootstrapChocolateyAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Does_not_bootstrap_when_already_installed()
    {
        var os = ChocoOs(installed: true);

        await Apply(CreateModule(), os, WithPackages("7zip"));

        await os.DidNotReceive().BootstrapChocolateyAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Install_chocolatey_false_and_missing_skips_with_ok()
    {
        var os = ChocoOs(installed: false);
        var config = new CloudConfigModel
        {
            Chocolatey = new ChocolateyConfig
            {
                InstallChocolatey = false,
                Packages = [new ChocolateyPackage { Name = "7zip" }],
            },
        };

        var outcome = await Apply(CreateModule(), os, config);

        outcome.Should().BeOfType<ModuleOutcome.Completed>();
        await os.DidNotReceive().BootstrapChocolateyAsync(Arg.Any<CancellationToken>());
        await os.DidNotReceive().RunChocoAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Bootstrap_reboot_returns_RebootRequested_before_installing()
    {
        var os = ChocoOs(installed: false, bootstrapReboot: true);

        var outcome = await Apply(CreateModule(), os, WithPackages("7zip"));

        outcome.Should().BeOfType<ModuleOutcome.RebootRequested>();
        // Packages are deferred to the post-reboot re-entry.
        await os.DidNotReceive().RunChocoAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Bootstrap_failure_returns_Fail()
    {
        var os = ChocoOs(installed: false);
        os.BootstrapChocolateyAsync(Arg.Any<CancellationToken>())
            .Returns<ChocolateyBootstrapResult>(_ => throw new InvalidOperationException("no internet"));

        var outcome = await Apply(CreateModule(), os, WithPackages("7zip"));

        outcome.Should().BeOfType<ModuleOutcome.Failed>();
        await os.DidNotReceive().RunChocoAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Installs_each_package_with_expected_argv()
    {
        var os = ChocoOs(installed: true);
        var config = new CloudConfigModel
        {
            Chocolatey = new ChocolateyConfig
            {
                Packages =
                [
                    new ChocolateyPackage { Name = "git" },
                    new ChocolateyPackage { Name = "7zip", Version = "24.07", Params = "/x" },
                ],
            },
        };

        var outcome = await Apply(CreateModule(), os, config);

        outcome.Should().BeOfType<ModuleOutcome.Completed>();
        await os.Received().RunChocoAsync(
            Arg.Is<IReadOnlyList<string>>(a => a.Contains("install") && a.Contains("git") && a.Contains("-y")),
            Arg.Any<CancellationToken>());
        await os.Received().RunChocoAsync(
            Arg.Is<IReadOnlyList<string>>(a =>
                a.Contains("7zip") && a.Contains("--version") && a.Contains("24.07")
                && a.Contains("--params=/x")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Params_with_spaces_and_colon_stay_one_argv_element()
    {
        var os = ChocoOs(installed: true);
        var config = new CloudConfigModel
        {
            Chocolatey = new ChocolateyConfig
            {
                Packages = [new ChocolateyPackage { Name = "7zip", Params = "/InstallDir:C:\\tools 7zip" }],
            },
        };

        await Apply(CreateModule(), os, config);

        await os.Received().RunChocoAsync(
            Arg.Is<IReadOnlyList<string>>(a => a.Contains("--params=/InstallDir:C:\\tools 7zip")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Package_reboot_takes_precedence_over_another_package_failure()
    {
        // One package fails, another requests a reboot. The reboot must win so
        // reboot-and-continue re-enters and retries the failed package.
        var os = Substitute.For<IWindowsOs>();
        os.IsChocolateyInstalledAsync(Arg.Any<CancellationToken>()).Returns(true);
        os.RunChocoAsync(Arg.Is<IReadOnlyList<string>>(a => a.Contains("badpkg")), Arg.Any<CancellationToken>())
            .Returns(new RunCommandResult(1, "", ""));
        os.RunChocoAsync(Arg.Is<IReadOnlyList<string>>(a => a.Contains("rebootpkg")), Arg.Any<CancellationToken>())
            .Returns(new RunCommandResult(3010, "", ""));

        var outcome = await Apply(CreateModule(), os, WithPackages("badpkg", "rebootpkg"));

        outcome.Should().BeOfType<ModuleOutcome.RebootRequested>();
    }

    [Theory]
    [InlineData(1641)]
    [InlineData(3010)]
    public async Task Package_reboot_exit_codes_return_RebootRequested(int exitCode)
    {
        var os = ChocoOs(installed: true, installExitCode: exitCode);

        var outcome = await Apply(CreateModule(), os, WithPackages("somepkg"));

        outcome.Should().BeOfType<ModuleOutcome.RebootRequested>();
    }

    [Fact]
    public async Task Package_install_failure_returns_Fail()
    {
        var os = ChocoOs(installed: true, installExitCode: 1);

        var outcome = await Apply(CreateModule(), os, WithPackages("somepkg"));

        outcome.Should().BeOfType<ModuleOutcome.Failed>();
    }

    [Fact]
    public async Task Registers_sources_with_expected_argv()
    {
        var os = ChocoOs(installed: true);
        var config = new CloudConfigModel
        {
            Chocolatey = new ChocolateyConfig
            {
                Sources = [new ChocolateySource { Name = "internal", Url = "https://nuget.example/choco", Priority = 2 }],
                Packages = [new ChocolateyPackage { Name = "7zip" }],
            },
        };

        await Apply(CreateModule(), os, config);

        await os.Received().RunChocoAsync(
            Arg.Is<IReadOnlyList<string>>(a =>
                a.Contains("source") && a.Contains("add") && a.Contains("internal")
                && a.Contains("--priority") && a.Contains("2")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Empty_chocolatey_block_still_bootstraps()
    {
        // chocolatey: {} with install defaulting true is a "just install choco"
        // request — must still bootstrap (regression guard).
        var os = ChocoOs(installed: false);
        var config = new CloudConfigModel { Chocolatey = new ChocolateyConfig() };

        var outcome = await Apply(CreateModule(), os, config);

        outcome.Should().BeOfType<ModuleOutcome.Completed>();
        await os.Received().BootstrapChocolateyAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Invalid_choco_name_is_skipped_not_run()
    {
        var os = ChocoOs(installed: true);
        var config = new CloudConfigModel
        {
            Chocolatey = new ChocolateyConfig { Packages = [new ChocolateyPackage { Name = "--version" }] },
        };

        var outcome = await Apply(CreateModule(), os, config);

        outcome.Should().BeOfType<ModuleOutcome.Completed>();
        await os.DidNotReceive().RunChocoAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Umbrella_chocolatey_entries_install_without_a_chocolatey_block()
    {
        // packages: [{chocolatey: [nodejs]}] with no chocolatey: block — the
        // module must still bootstrap choco and install the package.
        var os = ChocoOs(installed: true);
        var config = new CloudConfigModel
        {
            Packages =
            [
                new PackageEntry
                {
                    Managers = new Dictionary<string, IReadOnlyList<ManagerPackage>>
                    {
                        ["chocolatey"] = [new ManagerPackage { Name = "nodejs", Version = "20.11.0" }],
                    },
                },
            ],
        };

        var outcome = await Apply(CreateModule(), os, config);

        outcome.Should().BeOfType<ModuleOutcome.Completed>();
        await os.Received().RunChocoAsync(
            Arg.Is<IReadOnlyList<string>>(a => a.Contains("nodejs") && a.Contains("--version") && a.Contains("20.11.0")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Bare_umbrella_names_do_not_go_to_chocolatey()
    {
        // A bare name is the default provider (winget), so ChocolateyModule sees
        // no work and must not touch choco.
        var os = ChocoOs(installed: true);
        var config = new CloudConfigModel { Packages = [new PackageEntry { Name = "7zip.7zip" }] };

        var outcome = await Apply(CreateModule(), os, config);

        outcome.Should().BeOfType<ModuleOutcome.Completed>();
        await os.DidNotReceive().IsChocolateyInstalledAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Empty_package_name_is_a_validation_Fail_before_any_os_call()
    {
        var os = ChocoOs(installed: true);
        var config = new CloudConfigModel
        {
            Chocolatey = new ChocolateyConfig { Packages = [new ChocolateyPackage { Name = "  " }] },
        };

        var outcome = await Apply(CreateModule(), os, config);

        outcome.Should().BeOfType<ModuleOutcome.Failed>();
        await os.DidNotReceive().IsChocolateyInstalledAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public async Task Source_priority_out_of_range_is_a_validation_Fail(int priority)
    {
        var os = ChocoOs(installed: true);
        var config = new CloudConfigModel
        {
            Chocolatey = new ChocolateyConfig
            {
                Sources = [new ChocolateySource { Name = "x", Url = "https://x", Priority = priority }],
            },
        };

        var outcome = await Apply(CreateModule(), os, config);

        outcome.Should().BeOfType<ModuleOutcome.Failed>();
        await os.DidNotReceive().IsChocolateyInstalledAsync(Arg.Any<CancellationToken>());
    }
}
