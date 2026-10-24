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

        // /d /s /c "<...>" — /s makes cmd strip exactly the first and last quote.
        line.Should().StartWith("/d /s /c \"").And.EndWith("\"");
        line.Should().Be("/d /s /c \"\"C:\\tools\\npm.cmd\"\"");
    }

    [Fact]
    public void Force_quotes_every_argument()
    {
        var line = WindowsOs.BuildCmdBatchArguments(@"C:\tools\x.cmd", [@"C:\tools\x.cmd", "install", "foo"]);

        line.Should().Be("/d /s /c \"\"C:\\tools\\x.cmd\" \"install\" \"foo\"\"");
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

        line.Should().Be("/d /s /c \"\"C:\\Program Files\\x.cmd\" \"arg with space\"\"");
    }

    [Fact]
    public void Escapes_embedded_quotes_with_a_backslash()
    {
        var line = WindowsOs.BuildCmdBatchArguments(@"C:\tools\x.cmd", [@"C:\tools\x.cmd", "a\"b"]);

        line.Should().Contain("\"a\\\"b\"");
    }

    [Fact]
    public void Doubles_trailing_backslashes_before_the_closing_quote()
    {
        // A trailing backslash must not escape the closing quote the MSVCRT
        // tokenizer sees, so it is doubled.
        var line = WindowsOs.BuildCmdBatchArguments(@"C:\tools\x.cmd", [@"C:\tools\x.cmd", @"a\"]);

        line.Should().Contain("\"a\\\\\"");
    }

    [Theory]
    [InlineData("%PATH%")]
    [InlineData("a\nb")]
    [InlineData("a\rb")]
    public void Rejects_arguments_that_cmd_cannot_escape(string argument)
    {
        // '%' (variable expansion) and CR/LF cannot be escaped on a cmd command
        // line even inside quotes — reject rather than silently corrupt.
        var act = () => WindowsOs.BuildCmdBatchArguments(@"C:\tools\x.cmd", [@"C:\tools\x.cmd", argument]);

        act.Should().Throw<ArgumentException>();
    }
}
