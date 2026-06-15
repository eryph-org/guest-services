using AwesomeAssertions;
using Eryph.GuestServices.CloudConfig;
using Eryph.GuestServices.Provisioning.Update;

namespace Eryph.GuestServices.Provisioning.Tests.Update;

public sealed class UpdateTargetResolverTests
{
    private static ReleaseFile WinZip(string version) => new()
    {
        Filename = $"eryph_guest-services_{version}_windows_amd64.zip",
        Url = $"https://releases.example/{version}/win.zip",
        Sha256Checksum = "abc123",
        Os = "windows",
        Arch = "amd64",
    };

    private static ReleaseIndex IndexWith(
        string latest, string latestStable, params string[] versions)
    {
        var dict = versions.ToDictionary(
            v => v,
            v => new ReleaseVersion { Version = v, Files = [WinZip(v)] });
        return new ReleaseIndex
        {
            LatestVersion = latest,
            LatestStableVersion = latestStable,
            Versions = dict,
        };
    }

    [Fact]
    public void Null_config_is_no_update()
    {
        var decision = UpdateTargetResolver.Resolve(IndexWith("0.5.0", "0.4.0", "0.4.0"), null, "0.3.0");
        decision.ShouldUpdate.Should().BeFalse();
    }

    [Fact]
    public void Not_enabled_is_no_update()
    {
        var index = IndexWith("0.5.0", "0.4.0", "0.4.0");
        UpdateTargetResolver.Resolve(index, new EgsUpdateConfig { Enabled = false }, "0.3.0")
            .ShouldUpdate.Should().BeFalse();
        UpdateTargetResolver.Resolve(index, new EgsUpdateConfig { Version = "0.4.0" }, "0.3.0")
            .ShouldUpdate.Should().BeFalse(); // enabled omitted
    }

    [Fact]
    public void Enabled_stable_channel_targets_latest_stable()
    {
        var index = IndexWith("0.5.0-preview.1", "0.4.0", "0.4.0", "0.5.0-preview.1");
        var decision = UpdateTargetResolver.Resolve(
            index, new EgsUpdateConfig { Enabled = true, Channel = "stable" }, "0.3.0");

        decision.ShouldUpdate.Should().BeTrue();
        decision.TargetVersion.Should().Be("0.4.0");
        decision.File!.Url.Should().Be("https://releases.example/0.4.0/win.zip");
    }

    [Fact]
    public void Default_channel_is_stable()
    {
        var index = IndexWith("0.5.0-preview.1", "0.4.0", "0.4.0", "0.5.0-preview.1");
        UpdateTargetResolver.Resolve(index, new EgsUpdateConfig { Enabled = true }, "0.3.0")
            .TargetVersion.Should().Be("0.4.0");
    }

    [Fact]
    public void Unknown_channel_falls_back_to_stable_never_preview()
    {
        var index = IndexWith("0.5.0-preview.1", "0.4.0", "0.4.0", "0.5.0-preview.1");
        UpdateTargetResolver.Resolve(
                index, new EgsUpdateConfig { Enabled = true, Channel = "bogus" }, "0.3.0")
            .TargetVersion.Should().Be("0.4.0");
    }

    [Fact]
    public void Unstable_channel_targets_latest()
    {
        var index = IndexWith("0.5.0-preview.1", "0.4.0", "0.4.0", "0.5.0-preview.1");
        UpdateTargetResolver.Resolve(
                index, new EgsUpdateConfig { Enabled = true, Channel = "unstable" }, "0.3.0")
            .TargetVersion.Should().Be("0.5.0-preview.1");
    }

    [Fact]
    public void Pinned_version_wins_over_channel()
    {
        var index = IndexWith("0.5.0-preview.1", "0.4.0", "0.3.5", "0.4.0", "0.5.0-preview.1");
        UpdateTargetResolver.Resolve(
                index, new EgsUpdateConfig { Enabled = true, Version = "0.3.5", Channel = "unstable" }, "0.3.0")
            .TargetVersion.Should().Be("0.3.5");
    }

    [Fact]
    public void Already_current_is_no_update_ignoring_build_metadata()
    {
        var index = IndexWith("0.4.0", "0.4.0", "0.4.0");
        // Running version carries the +Branch.x.Sha.y suffix; must still match.
        UpdateTargetResolver.Resolve(
                index, new EgsUpdateConfig { Enabled = true, Version = "0.4.0" },
                "0.4.0+Branch.main.Sha.deadbeef")
            .ShouldUpdate.Should().BeFalse();
    }

    [Fact]
    public void Explicit_downgrade_pin_is_an_update()
    {
        var index = IndexWith("0.5.0", "0.5.0", "0.4.0", "0.5.0");
        var decision = UpdateTargetResolver.Resolve(
            index, new EgsUpdateConfig { Enabled = true, Version = "0.4.0" }, "0.5.0");
        decision.ShouldUpdate.Should().BeTrue();
        decision.TargetVersion.Should().Be("0.4.0");
    }

    [Fact]
    public void Target_absent_from_index_is_no_update()
    {
        var index = IndexWith("0.4.0", "0.4.0", "0.4.0");
        UpdateTargetResolver.Resolve(
                index, new EgsUpdateConfig { Enabled = true, Version = "9.9.9" }, "0.3.0")
            .ShouldUpdate.Should().BeFalse();
    }

    [Fact]
    public void Target_without_windows_package_is_no_update()
    {
        var index = new ReleaseIndex
        {
            LatestStableVersion = "0.4.0",
            Versions = new Dictionary<string, ReleaseVersion>
            {
                ["0.4.0"] = new()
                {
                    Version = "0.4.0",
                    Files =
                    [
                        new ReleaseFile { Filename = "linux.tar.gz", Url = "u", Sha256Checksum = "h", Os = "linux", Arch = "amd64" },
                        new ReleaseFile { Filename = "img.iso", Url = "u", Sha256Checksum = "h", Os = "windows", Arch = "amd64", Tags = ["iso"] },
                    ],
                },
            },
        };
        UpdateTargetResolver.Resolve(index, new EgsUpdateConfig { Enabled = true }, "0.3.0")
            .ShouldUpdate.Should().BeFalse();
    }

    [Fact]
    public void SelectWindowsPackage_picks_windows_amd64_zip_excluding_iso()
    {
        var files = new[]
        {
            new ReleaseFile { Filename = "linux.tar.gz", Os = "linux", Arch = "amd64" },
            new ReleaseFile { Filename = "win.iso", Os = "windows", Arch = "amd64", Tags = ["iso"] },
            new ReleaseFile { Filename = "win.zip", Os = "windows", Arch = "amd64" },
        };
        UpdateTargetResolver.SelectWindowsPackage(files)!.Filename.Should().Be("win.zip");
    }
}
