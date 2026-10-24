using AwesomeAssertions;
using Eryph.GuestServices.Provisioning.Windows;

namespace Eryph.GuestServices.Provisioning.Tests.Windows;

/// <summary>
/// The cmd.exe command-line builder for batch dispatch is a pure string factory,
/// so its quoting can be asserted on any host (it also runs on the Linux CI leg).
/// cmd re-parses the line with different rules than the MSVCRT quoting that
/// ProcessStartInfo.ArgumentList emits, so this locks in the hand-rolled quoting.
/// </summary>
public sealed class BuildCmdBatchArgumentsTests
{
    [Fact]
    public void Wraps_the_whole_command_in_a_single_outer_quote_pair()
    {
        var line = WindowsOs.BuildCmdBatchArguments(@"C:\tools\npm.cmd", [@"C:\tools\npm.cmd"]);

        // /d /s /c "<...>" — /s makes cmd strip exactly the first and last quote;
        // chcp 65001 switches the session to UTF-8 before the batch runs.
        line.Should().StartWith("/d /s /c \"chcp 65001>nul & ").And.EndWith("\"");
        line.Should().Be("/d /s /c \"chcp 65001>nul & \"C:\\tools\\npm.cmd\"\"");
    }

    [Fact]
    public void Force_quotes_every_argument()
    {
        var line = WindowsOs.BuildCmdBatchArguments(@"C:\tools\x.cmd", [@"C:\tools\x.cmd", "install", "foo"]);

        line.Should().Be("/d /s /c \"chcp 65001>nul & \"C:\\tools\\x.cmd\" \"install\" \"foo\"\"");
    }

    [Fact]
    public void Keeps_cmd_metacharacters_inside_quotes()
    {
        var line = WindowsOs.BuildCmdBatchArguments(@"C:\tools\x.cmd", [@"C:\tools\x.cmd", "a&b"]);

        // The '&' is enclosed in quotes, so cmd cannot read it as a separator.
        line.Should().Contain("\"a&b\"");
    }

    [Fact]
    public void Quotes_arguments_and_paths_that_contain_spaces()
    {
        var line = WindowsOs.BuildCmdBatchArguments(@"C:\Program Files\x.cmd", [@"C:\Program Files\x.cmd", "arg with space"]);

        line.Should().Be("/d /s /c \"chcp 65001>nul & \"C:\\Program Files\\x.cmd\" \"arg with space\"\"");
    }

    [Fact]
    public void Preserves_backslashes_literally()
    {
        // cmd does not treat backslash as a quote escape, so it is passed
        // through unchanged (not doubled). The closing quote still terminates
        // the token at the cmd layer regardless of a trailing backslash.
        var line = WindowsOs.BuildCmdBatchArguments(@"C:\tools\x.cmd", [@"C:\tools\x.cmd", @"a\b\"]);

        line.Should().Be("/d /s /c \"chcp 65001>nul & \"C:\\tools\\x.cmd\" \"a\\b\\\"\"");
    }

    [Theory]
    [InlineData("a\"b")]                      // an embedded quote would close the pair early
    [InlineData("a\"&echo UNEXPECTED&rem ")]  // ... and expose the rest as live cmd syntax
    [InlineData("%PATH%")]                    // variable expansion, not escapable on a cmd line
    [InlineData("a\nb")]
    [InlineData("a\rb")]
    public void Rejects_arguments_that_cmd_cannot_escape(string argument)
    {
        // A double quote, '%' and CR/LF cannot be safely carried through
        // cmd /c — reject rather than corrupt or (for the quote) mis-execute.
        var act = () => WindowsOs.BuildCmdBatchArguments(@"C:\tools\x.cmd", [@"C:\tools\x.cmd", argument]);

        act.Should().Throw<ArgumentException>();
    }
}
