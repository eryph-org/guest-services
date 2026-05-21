using AwesomeAssertions;
using Eryph.GuestServices.Provisioning.Windows;
using Microsoft.Extensions.Logging.Abstractions;

namespace Eryph.GuestServices.Provisioning.Tests.Windows;

/// <summary>
/// Tests for the pure (non-IO) parts of <see cref="WindowsOs"/>. The remaining
/// methods mutate the host and are tagged <c>Category=Manual</c> below.
/// </summary>
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

    // The following tests genuinely mutate the host and need a live Windows
    // instance with admin privileges. We keep them around as a manual smoke
    // suite — they are not run by default.

    [Fact]
    [Trait("Category", "Manual")]
    public async Task GetComputerNameAsync_returns_environment_machine_name()
    {
        var name = await CreateOs().GetComputerNameAsync(CancellationToken.None);
        name.Should().Be(Environment.MachineName);
    }

    [Fact(Skip = "Mutates the host; run manually.")]
    [Trait("Category", "Manual")]
    public async Task SetComputerNameAsync_returns_AlreadySet_for_current_name()
    {
        var os = CreateOs();
        var current = await os.GetComputerNameAsync(CancellationToken.None);
        var result = await os.SetComputerNameAsync(current, CancellationToken.None);
        result.Should().Be(SetComputerNameResult.AlreadySet);
    }

    [Fact(Skip = "Spawns a real process; manual.")]
    [Trait("Category", "Manual")]
    public async Task RunShellCommandAsync_executes_via_cmd()
    {
        var result = await CreateOs().RunShellCommandAsync("echo hello", CancellationToken.None);
        result.ExitCode.Should().Be(0);
        result.StdOut.Trim().Should().Be("hello");
    }
}
