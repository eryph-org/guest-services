using AwesomeAssertions;
using Eryph.GuestServices.Core;

namespace Eryph.GuestServices.DevTunnels.Ssh.Extensions.Tests;

public class DefaultShellSelectorTests
{
    [Fact]
    public async Task SelectAsync_WithSshShellEnv_UsesIt()
    {
        var env = new Dictionary<string, string>
        {
            [Constants.ShellEnvName] = "pwsh.exe",
            [Constants.ShellArgsEnvName] = "-NoLogo",
        };

        var selection = await DefaultShellSelector.Instance.SelectAsync(env, CancellationToken.None);

        selection.Command.Should().Be("pwsh.exe");
        selection.Arguments.Should().Be("-NoLogo");
    }

    [Fact]
    public async Task SelectAsync_WithShellEnvButNoArgs_UsesEmptyArgs()
    {
        var env = new Dictionary<string, string>
        {
            [Constants.ShellEnvName] = "pwsh.exe",
        };

        var selection = await DefaultShellSelector.Instance.SelectAsync(env, CancellationToken.None);

        selection.Command.Should().Be("pwsh.exe");
        selection.Arguments.Should().BeEmpty();
    }

    [Fact]
    public async Task SelectAsync_WithBlankShellEnv_FallsBackToPlatformDefault()
    {
        var env = new Dictionary<string, string>
        {
            [Constants.ShellEnvName] = "   ",
        };

        var selection = await DefaultShellSelector.Instance.SelectAsync(env, CancellationToken.None);

        selection.Should().Be(DefaultShellSelector.PlatformDefault());
    }

    [Fact]
    public async Task SelectAsync_WithEmptyEnv_ReturnsPlatformDefault()
    {
        var env = new Dictionary<string, string>();

        var selection = await DefaultShellSelector.Instance.SelectAsync(env, CancellationToken.None);

        selection.Should().Be(DefaultShellSelector.PlatformDefault());
    }

    [Fact]
    public void PlatformDefault_OnWindows_IsPowerShell()
    {
        // The test process always runs net10.0 (Linux job too); however the
        // selector branches on OperatingSystem.IsLinux/.IsWindows at runtime.
        // Skip when not on Windows to avoid asserting on the wrong branch.
        if (!OperatingSystem.IsWindows())
            return;

        var selection = DefaultShellSelector.PlatformDefault();

        selection.Command.Should().Be("powershell.exe");
        selection.Arguments.Should().Be("-WindowStyle Hidden");
    }

    [Fact]
    public void PlatformDefault_OnLinux_UsesShellEnvOrBash()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var selection = DefaultShellSelector.PlatformDefault();

        selection.Arguments.Should().Be("-i");
        // Either honors $SHELL or falls back to /bin/bash; both are acceptable.
        selection.Command.Should().NotBeNullOrWhiteSpace();
    }
}
