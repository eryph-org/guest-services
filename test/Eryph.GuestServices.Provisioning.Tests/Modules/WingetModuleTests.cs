using AwesomeAssertions;
using Eryph.GuestServices.CloudConfig;
using Eryph.GuestServices.Provisioning.Modules;
using Eryph.GuestServices.Provisioning.UserData;
using Eryph.GuestServices.Provisioning.Windows;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using CloudConfigModel = global::Eryph.GuestServices.CloudConfig.CloudConfig;

namespace Eryph.GuestServices.Provisioning.Tests.Modules;

public sealed class WingetModuleTests
{
    private static WingetModule CreateModule() => new(NullLogger<WingetModule>.Instance);

    private static IWindowsOs WingetOs(bool provisioned, int installExitCode = 0)
    {
        var os = Substitute.For<IWindowsOs>();
        os.IsWingetProvisionedAsync(Arg.Any<CancellationToken>()).Returns(provisioned);
        os.RunWingetAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new RunCommandResult(installExitCode, "", ""));
        return os;
    }

    private static Task<ModuleOutcome> Apply(WingetModule module, IWindowsOs os, CloudConfigModel config) =>
        module.ApplyAsync(ResolvedUserData.Empty(config), new TestModuleContext(os), CancellationToken.None);

    [Fact]
    public async Task No_winget_block_is_a_noop()
    {
        var os = Substitute.For<IWindowsOs>();

        var outcome = await Apply(CreateModule(), os, new CloudConfigModel());

        outcome.Should().BeOfType<ModuleOutcome.Completed>();
        await os.DidNotReceive().IsWingetProvisionedAsync(Arg.Any<CancellationToken>());
        await os.DidNotReceive().ProvisionWingetAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Provisions_winget_when_missing_and_install_defaulted()
    {
        var os = WingetOs(provisioned: false);
        var config = new CloudConfigModel
        {
            Winget = new WingetConfig { Packages = [new WingetPackage { Id = "Git.Git" }] },
        };

        var outcome = await Apply(CreateModule(), os, config);

        outcome.Should().BeOfType<ModuleOutcome.Completed>();
        await os.Received().ProvisionWingetAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Does_not_provision_when_already_present()
    {
        var os = WingetOs(provisioned: true);
        var config = new CloudConfigModel
        {
            Winget = new WingetConfig { Packages = [new WingetPackage { Id = "Git.Git" }] },
        };

        await Apply(CreateModule(), os, config);

        await os.DidNotReceive().ProvisionWingetAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Install_winget_false_and_missing_skips_with_ok()
    {
        var os = WingetOs(provisioned: false);
        var config = new CloudConfigModel
        {
            Winget = new WingetConfig
            {
                InstallWinget = false,
                Packages = [new WingetPackage { Id = "Git.Git" }],
            },
        };

        var outcome = await Apply(CreateModule(), os, config);

        outcome.Should().BeOfType<ModuleOutcome.Completed>();
        await os.DidNotReceive().ProvisionWingetAsync(Arg.Any<CancellationToken>());
        await os.DidNotReceive().EnsureWingetWrapperAsync(Arg.Any<CancellationToken>());
        await os.DidNotReceive().RunWingetAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Installs_each_package_with_expected_argv()
    {
        var os = WingetOs(provisioned: true);
        var config = new CloudConfigModel
        {
            Winget = new WingetConfig
            {
                Packages =
                [
                    new WingetPackage { Id = "Git.Git" },
                    new WingetPackage { Id = "7zip.7zip", Version = "24.07" },
                ],
            },
        };

        var outcome = await Apply(CreateModule(), os, config);

        outcome.Should().BeOfType<ModuleOutcome.Completed>();
        await os.Received().RunWingetAsync(
            Arg.Is<IReadOnlyList<string>>(a =>
                a.Contains("install") && a.Contains("Git.Git")
                && a.Contains("--scope") && a.Contains("machine")
                && a.Contains("--source") && a.Contains("winget")),
            Arg.Any<CancellationToken>());
        // Pinned version adds --version <v> but NOT --force.
        await os.Received().RunWingetAsync(
            Arg.Is<IReadOnlyList<string>>(a =>
                a.Contains("7zip.7zip") && a.Contains("--version") && a.Contains("24.07")),
            Arg.Any<CancellationToken>());
        await os.DidNotReceive().RunWingetAsync(
            Arg.Is<IReadOnlyList<string>>(a => a.Contains("--force")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Ensures_wrapper_even_when_winget_already_provisioned()
    {
        // Regression for the Win11 / Server 2025 case: winget ships provisioned,
        // so ProvisionWingetAsync is skipped — but the LocalSystem wrapper must
        // still be written before any RunWingetAsync call.
        var os = WingetOs(provisioned: true);
        var config = new CloudConfigModel
        {
            Winget = new WingetConfig { Packages = [new WingetPackage { Id = "Git.Git" }] },
        };

        await Apply(CreateModule(), os, config);

        await os.DidNotReceive().ProvisionWingetAsync(Arg.Any<CancellationToken>());
        await os.Received().EnsureWingetWrapperAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Wrapper_preparation_failure_returns_Fail()
    {
        var os = WingetOs(provisioned: true);
        os.EnsureWingetWrapperAsync(Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("winget does not run as LocalSystem"));
        var config = new CloudConfigModel
        {
            Winget = new WingetConfig { Packages = [new WingetPackage { Id = "Git.Git" }] },
        };

        var outcome = await Apply(CreateModule(), os, config);

        outcome.Should().BeOfType<ModuleOutcome.Failed>();
        await os.DidNotReceive().RunWingetAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Case_insensitive_source_and_scope_are_accepted()
    {
        var os = WingetOs(provisioned: true);
        var config = new CloudConfigModel
        {
            Winget = new WingetConfig
            {
                Packages = [new WingetPackage { Id = "Git.Git", Source = "WINGET", Scope = "Machine" }],
            },
        };

        var outcome = await Apply(CreateModule(), os, config);

        outcome.Should().BeOfType<ModuleOutcome.Completed>();
        await os.Received().RunWingetAsync(
            Arg.Is<IReadOnlyList<string>>(a => a.Contains("Git.Git") && a.Contains("install")),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(unchecked((int)0x8A150061))] // already installed (-1978335135)
    [InlineData(unchecked((int)0x8A15002B))] // no newer version available (-1978335189)
    public async Task Benign_winget_exit_codes_are_success(int exitCode)
    {
        var os = WingetOs(provisioned: true, installExitCode: exitCode);
        var config = new CloudConfigModel
        {
            Winget = new WingetConfig { Packages = [new WingetPackage { Id = "Git.Git" }] },
        };

        var outcome = await Apply(CreateModule(), os, config);

        outcome.Should().BeOfType<ModuleOutcome.Completed>();
    }

    [Fact]
    public async Task Install_failure_returns_Fail()
    {
        var os = WingetOs(provisioned: true, installExitCode: 1);
        var config = new CloudConfigModel
        {
            Winget = new WingetConfig { Packages = [new WingetPackage { Id = "Git.Git" }] },
        };

        var outcome = await Apply(CreateModule(), os, config);

        outcome.Should().BeOfType<ModuleOutcome.Failed>();
    }

    [Fact]
    public async Task Provision_failure_returns_Fail()
    {
        var os = WingetOs(provisioned: false);
        os.ProvisionWingetAsync(Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("Server 2019 unsupported"));
        var config = new CloudConfigModel
        {
            Winget = new WingetConfig { Packages = [new WingetPackage { Id = "Git.Git" }] },
        };

        var outcome = await Apply(CreateModule(), os, config);

        outcome.Should().BeOfType<ModuleOutcome.Failed>();
        await os.DidNotReceive().RunWingetAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("msstore", null)]
    [InlineData(null, "user")]
    public async Task Unsupported_source_or_scope_is_skipped_not_failed(string? source, string? scope)
    {
        var os = WingetOs(provisioned: true);
        var config = new CloudConfigModel
        {
            Winget = new WingetConfig
            {
                Packages = [new WingetPackage { Id = "Some.Pkg", Source = source, Scope = scope }],
            },
        };

        var outcome = await Apply(CreateModule(), os, config);

        outcome.Should().BeOfType<ModuleOutcome.Completed>();
        await os.DidNotReceive().RunWingetAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Empty_winget_block_still_provisions_winget()
    {
        // winget: {} (no packages/sources) with install defaulting true is a
        // "just install winget" request — must still provision (regression guard).
        var os = WingetOs(provisioned: false);
        var config = new CloudConfigModel { Winget = new WingetConfig() };

        var outcome = await Apply(CreateModule(), os, config);

        outcome.Should().BeOfType<ModuleOutcome.Completed>();
        await os.Received().ProvisionWingetAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Invalid_winget_id_is_skipped_not_run()
    {
        var os = WingetOs(provisioned: true);
        var config = new CloudConfigModel
        {
            Winget = new WingetConfig { Packages = [new WingetPackage { Id = "--source=evil" }] },
        };

        var outcome = await Apply(CreateModule(), os, config);

        outcome.Should().BeOfType<ModuleOutcome.Completed>();
        await os.DidNotReceive().RunWingetAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Umbrella_packages_install_without_a_winget_block()
    {
        // packages: [7zip.7zip] with no winget: block — the module must still
        // provision winget and install the default-provider package.
        var os = WingetOs(provisioned: true);
        var config = new CloudConfigModel { Packages = [new PackageEntry { Name = "7zip.7zip" }] };

        var outcome = await Apply(CreateModule(), os, config);

        outcome.Should().BeOfType<ModuleOutcome.Completed>();
        await os.Received().EnsureWingetWrapperAsync(Arg.Any<CancellationToken>());
        await os.Received().RunWingetAsync(
            Arg.Is<IReadOnlyList<string>>(a => a.Contains("install") && a.Contains("7zip.7zip")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Winget_block_and_umbrella_packages_are_both_installed()
    {
        var os = WingetOs(provisioned: true);
        var config = new CloudConfigModel
        {
            Winget = new WingetConfig { Packages = [new WingetPackage { Id = "Git.Git" }] },
            Packages =
            [
                new PackageEntry
                {
                    Managers = new Dictionary<string, IReadOnlyList<ManagerPackage>>
                    {
                        ["winget"] = [new ManagerPackage { Name = "7zip.7zip" }],
                    },
                },
            ],
        };

        await Apply(CreateModule(), os, config);

        await os.Received().RunWingetAsync(
            Arg.Is<IReadOnlyList<string>>(a => a.Contains("Git.Git")), Arg.Any<CancellationToken>());
        await os.Received().RunWingetAsync(
            Arg.Is<IReadOnlyList<string>>(a => a.Contains("7zip.7zip")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Custom_source_package_is_installed_from_that_source()
    {
        // A registered custom winget source works under LocalSystem (only msstore
        // needs a user token), so a package targeting it must install from it.
        var os = WingetOs(provisioned: true);
        var config = new CloudConfigModel
        {
            Winget = new WingetConfig
            {
                Sources = [new WingetSource { Name = "contoso", Argument = "https://winget.contoso.example/" }],
                Packages = [new WingetPackage { Id = "Contoso.Tool", Source = "contoso" }],
            },
        };

        var outcome = await Apply(CreateModule(), os, config);

        outcome.Should().BeOfType<ModuleOutcome.Completed>();
        await os.Received().RunWingetAsync(
            Arg.Is<IReadOnlyList<string>>(a =>
                a.Contains("Contoso.Tool") && a.Contains("--source") && a.Contains("contoso")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Multiword_custom_source_is_registered_and_installed_from()
    {
        // Regression: a custom source name with a space ("Internal Tools") is
        // accepted at registration and is a single argv value at install, so the
        // package must install from it — not be silently skipped.
        var os = WingetOs(provisioned: true);
        var config = new CloudConfigModel
        {
            Winget = new WingetConfig
            {
                Sources = [new WingetSource { Name = "Internal Tools", Argument = "https://winget.contoso.example/" }],
                Packages = [new WingetPackage { Id = "Contoso.Tool", Source = "Internal Tools" }],
            },
        };

        var outcome = await Apply(CreateModule(), os, config);

        outcome.Should().BeOfType<ModuleOutcome.Completed>();
        await os.Received().RunWingetAsync(
            Arg.Is<IReadOnlyList<string>>(a =>
                a.Contains("Contoso.Tool") && a.Contains("--source") && a.Contains("Internal Tools")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Sources_are_registered_before_packages()
    {
        var os = WingetOs(provisioned: true);
        var config = new CloudConfigModel
        {
            Winget = new WingetConfig
            {
                Sources = [new WingetSource { Name = "contoso", Argument = "https://example.invalid/winget" }],
                Packages = [new WingetPackage { Id = "Git.Git" }],
            },
        };

        await Apply(CreateModule(), os, config);

        await os.Received().RunWingetAsync(
            Arg.Is<IReadOnlyList<string>>(a => a.Contains("source") && a.Contains("add") && a.Contains("contoso")),
            Arg.Any<CancellationToken>());
    }
}
