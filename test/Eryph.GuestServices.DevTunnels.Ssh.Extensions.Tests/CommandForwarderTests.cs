using AwesomeAssertions;
using Eryph.GuestServices.DevTunnels.Ssh.Extensions.Forwarders;

namespace Eryph.GuestServices.DevTunnels.Ssh.Extensions.Tests;

/// <summary>
/// Regression tests for the SSH <c>exec</c> path. The remote command must be
/// run through the selected shell (OpenSSH's <c>$SHELL -c "&lt;command&gt;"</c>
/// contract) and handed over as a single argument — the previous
/// split-on-space exec'd the first token directly, so pipes, <c>&amp;&amp;</c>,
/// quoting and any path containing a space were silently broken.
/// </summary>
public class CommandForwarderTests
{
    [Fact]
    public void BuildStartInfo_RunsCommandThroughShell()
    {
        var startInfo = CommandForwarder.BuildStartInfo("/bin/bash", "echo hi");

        startInfo.FileName.Should().Be("/bin/bash");
        startInfo.ArgumentList.Should().Equal("-c", "echo hi");
    }

    [Fact]
    public void BuildStartInfo_KeepsPipelineAsSingleArgument()
    {
        const string command = "ls -la | grep foo && echo \"done now\"";

        var startInfo = CommandForwarder.BuildStartInfo("/bin/bash", command);

        // The whole pipeline is one argument so bash parses it — not our code.
        startInfo.ArgumentList.Should().Equal("-c", command);
    }

    [Fact]
    public void BuildStartInfo_UsesPowerShellCommandFlag()
    {
        const string command = "Get-Process | Where-Object Name -eq foo";

        var startInfo = CommandForwarder.BuildStartInfo("powershell.exe", command);

        startInfo.FileName.Should().Be("powershell.exe");
        startInfo.ArgumentList.Should().Equal("-Command", command);
    }

    [Fact]
    public void BuildStartInfo_RedirectsStdInAndStdOutWithoutShellExecute()
    {
        var startInfo = CommandForwarder.BuildStartInfo("/bin/bash", "echo hi");

        startInfo.UseShellExecute.Should().BeFalse();
        startInfo.RedirectStandardInput.Should().BeTrue();
        startInfo.RedirectStandardOutput.Should().BeTrue();
        startInfo.CreateNoWindow.Should().BeTrue();
    }

    [Fact]
    public void BuildStartInfo_RedirectsStdErr_SoErrorsReachTheClient()
    {
        // Regression: stderr was previously left attached to the service
        // process, so command errors never reached the SSH client.
        var startInfo = CommandForwarder.BuildStartInfo("/bin/bash", "echo hi");

        startInfo.RedirectStandardError.Should().BeTrue();
    }
}
