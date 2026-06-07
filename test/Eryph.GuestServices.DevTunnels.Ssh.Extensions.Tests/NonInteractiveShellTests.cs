using AwesomeAssertions;

namespace Eryph.GuestServices.DevTunnels.Ssh.Extensions.Tests;

public class NonInteractiveShellTests
{
    [Theory]
    // POSIX shells — bare names and absolute paths both use -c.
    [InlineData("bash", "-c")]
    [InlineData("/bin/bash", "-c")]
    [InlineData("/usr/bin/sh", "-c")]
    [InlineData("zsh", "-c")]
    [InlineData("/usr/bin/fish", "-c")]
    // PowerShell variants use -Command.
    [InlineData("pwsh", "-Command")]
    [InlineData("pwsh.exe", "-Command")]
    [InlineData("powershell.exe", "-Command")]
    [InlineData(@"C:\Program Files\PowerShell\7\pwsh.exe", "-Command")]
    // cmd uses /c.
    [InlineData("cmd", "/c")]
    [InlineData("cmd.exe", "/c")]
    [InlineData(@"C:\Windows\System32\cmd.exe", "/c")]
    // Unknown shells fall back to the POSIX -c form.
    [InlineData("some-custom-shell", "-c")]
    public void CommandFlagFor_MapsShellToFlag(string shellCommand, string expectedFlag)
    {
        NonInteractiveShell.CommandFlagFor(shellCommand).Should().Be(expectedFlag);
    }

    [Theory]
    [InlineData("POWERSHELL.EXE", "-Command")]
    [InlineData("PowerShell.Exe", "-Command")]
    [InlineData("CMD.EXE", "/c")]
    public void CommandFlagFor_IsCaseInsensitive(string shellCommand, string expectedFlag)
    {
        NonInteractiveShell.CommandFlagFor(shellCommand).Should().Be(expectedFlag);
    }

    [Theory]
    [InlineData("  pwsh.exe  ", "-Command")]
    [InlineData("  /bin/bash  ", "-c")]
    public void CommandFlagFor_TrimsSurroundingWhitespace(string shellCommand, string expectedFlag)
    {
        NonInteractiveShell.CommandFlagFor(shellCommand).Should().Be(expectedFlag);
    }
}
