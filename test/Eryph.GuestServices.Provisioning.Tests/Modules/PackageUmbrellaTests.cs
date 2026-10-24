using AwesomeAssertions;
using Eryph.GuestServices.CloudConfig;
using Eryph.GuestServices.Provisioning.Modules;
using CloudConfigModel = global::Eryph.GuestServices.CloudConfig.CloudConfig;

namespace Eryph.GuestServices.Provisioning.Tests.Modules;

public sealed class PackageUmbrellaTests
{
    private static CloudConfigModel Config(params PackageEntry[] entries) =>
        new() { Packages = entries };

    [Fact]
    public void Bare_names_and_winget_manager_go_to_winget_default_is_winget()
    {
        var config = Config(
            new PackageEntry { Name = "7zip.7zip" },
            new PackageEntry { Name = "Git.Git", Version = "2.46.0" },
            new PackageEntry
            {
                Managers = new Dictionary<string, IReadOnlyList<ManagerPackage>>
                {
                    ["winget"] = [new ManagerPackage { Name = "Microsoft.PowerShell", Version = "7.4.0" }],
                    ["chocolatey"] = [new ManagerPackage { Name = "nodejs" }],
                    ["apt"] = [new ManagerPackage { Name = "htop" }],
                },
            });

        var winget = PackageUmbrella.WingetPackages(config);

        winget.Select(p => p.Id).Should().BeEquivalentTo("7zip.7zip", "Git.Git", "Microsoft.PowerShell");
        winget.Single(p => p.Id == "Git.Git").Version.Should().Be("2.46.0");
        winget.Single(p => p.Id == "Microsoft.PowerShell").Version.Should().Be("7.4.0");
    }

    [Fact]
    public void Only_chocolatey_manager_entries_go_to_chocolatey()
    {
        var config = Config(
            new PackageEntry { Name = "7zip.7zip" }, // bare -> winget, not choco
            new PackageEntry
            {
                Managers = new Dictionary<string, IReadOnlyList<ManagerPackage>>
                {
                    ["chocolatey"] = [new ManagerPackage { Name = "nodejs", Version = "20.11.0" }],
                    ["apt"] = [new ManagerPackage { Name = "htop" }],
                },
            });

        var choco = PackageUmbrella.ChocolateyPackages(config);

        choco.Should().ContainSingle();
        choco[0].Name.Should().Be("nodejs");
        choco[0].Version.Should().Be("20.11.0");
    }

    [Fact]
    public void Choco_alias_is_accepted()
    {
        var config = Config(new PackageEntry
        {
            Managers = new Dictionary<string, IReadOnlyList<ManagerPackage>>
            {
                ["choco"] = [new ManagerPackage { Name = "git" }],
            },
        });

        PackageUmbrella.ChocolateyPackages(config).Should().ContainSingle(p => p.Name == "git");
        PackageUmbrella.WingetPackages(config).Should().BeEmpty();
    }

    [Fact]
    public void No_packages_yields_empty()
    {
        PackageUmbrella.WingetPackages(new CloudConfigModel()).Should().BeEmpty();
        PackageUmbrella.ChocolateyPackages(new CloudConfigModel()).Should().BeEmpty();
    }
}
