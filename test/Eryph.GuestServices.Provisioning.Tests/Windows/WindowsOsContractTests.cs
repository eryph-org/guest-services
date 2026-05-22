using System.Runtime.Versioning;
using AwesomeAssertions;
using Eryph.GuestServices.Provisioning.Windows;
using Microsoft.Extensions.Logging.Abstractions;

namespace Eryph.GuestServices.Provisioning.Tests.Windows;

/// <summary>
/// Tests for the pure (non-IO) parts of <see cref="WindowsOs"/>. The remaining
/// methods mutate the host and are tagged <c>Category=Manual</c> below.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsOsContractTests
{
    private static WindowsOs CreateOs() => new(NullLogger<WindowsOs>.Instance);

    [Theory]
    [InlineData("/etc/foo", @"C:\etc\foo")]
    [InlineData("/etc/cloud/cloud.cfg", @"C:\etc\cloud\cloud.cfg")]
    public void TranslateUnixPath_maps_etc_under_C(string input, string expected)
    {
        CreateOs().TranslateUnixPath(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("/home/alice", @"C:\Users\alice")]
    [InlineData("/home/alice/.ssh/authorized_keys", @"C:\Users\alice\.ssh\authorized_keys")]
    public void TranslateUnixPath_maps_home_to_Users(string input, string expected)
    {
        CreateOs().TranslateUnixPath(input).Should().Be(expected);
    }

    [Fact]
    public void TranslateUnixPath_maps_root_to_administrator_profile()
    {
        CreateOs().TranslateUnixPath("/root/.profile").Should().Be(@"C:\Users\Administrator\.profile");
    }

    [Theory]
    [InlineData(@"C:\already\windows", @"C:\already\windows")]
    [InlineData("C:/forward/slashes", @"C:\forward\slashes")]
    public void TranslateUnixPath_passes_through_drive_rooted_paths(string input, string expected)
    {
        CreateOs().TranslateUnixPath(input).Should().Be(expected);
    }

    [Fact]
    public void TranslateUnixPath_passes_through_unc_paths()
    {
        CreateOs().TranslateUnixPath(@"\\server\share\foo").Should().Be(@"\\server\share\foo");
    }

    [Fact]
    public void TranslateUnixPath_rejects_empty_input()
    {
        var act = () => CreateOs().TranslateUnixPath("");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void TranslateUnixPath_rejects_relative_paths()
    {
        var act = () => CreateOs().TranslateUnixPath("relative/path");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void TranslateUnixPath_handles_root()
    {
        CreateOs().TranslateUnixPath("/").Should().Be("C:\\");
    }

    [Theory]
    [InlineData("/../../Windows/notepad.exe")]
    [InlineData("/etc/../../foo")]
    [InlineData("/home/alice/../../bob")]
    [InlineData(@"C:\foo\..\Windows\System32")]
    public void TranslateUnixPath_rejects_parent_traversal(string input)
    {
        var act = () => CreateOs().TranslateUnixPath(input);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetComputerNameAsync_returns_environment_machine_name()
    {
        var name = await CreateOs().GetComputerNameAsync(CancellationToken.None);
        name.Should().Be(Environment.MachineName);
    }

    // SetComputerNameAsync's AlreadySet branch is exercised in the eryph-genes
    // base-catlet Pester suite (Validate-BaseOS.Tests.ps1) by observing the
    // SetHostname module's idempotent re-run in the egs-service event log.

    // Regression: cmd.exe /c "<complex-string-with-pipe>" has notoriously broken
    // quoting rules and mangled the runcmd payloads from real cloud-config
    // (powershell -Command "... | Out-Null") so the command ran but its `|` was
    // eaten by cmd. We now write to a temp .cmd file. This test must execute a
    // real process — it's quick, non-mutating, Windows-only.
    [Fact]
    [Trait("Category", "Integration")]
    public async Task RunShellCommandAsync_PreservesPipeInComplexCommand()
    {
        if (!OperatingSystem.IsWindows()) return;

        var marker = Path.Combine(Path.GetTempPath(), $"egs-pipe-test-{Guid.NewGuid():N}.txt");
        try
        {
            // The command contains a `|` that MUST be interpreted by powershell,
            // not by cmd.exe. Pre-fix this would silently drop the file write.
            var cmd = $"powershell -NoProfile -Command \"New-Item -ItemType Directory -Force '{Path.GetDirectoryName(marker)}' | Out-Null; Set-Content -LiteralPath '{marker}' -Value pipe-worked\"";
            var result = await CreateOs().RunShellCommandAsync(cmd, CancellationToken.None);
            result.ExitCode.Should().Be(0);
            File.Exists(marker).Should().BeTrue("the | inside the powershell -Command argument must reach powershell intact");
            (await File.ReadAllTextAsync(marker)).Trim().Should().Be("pipe-worked");
        }
        finally
        {
            try { File.Delete(marker); } catch { }
        }
    }
}
