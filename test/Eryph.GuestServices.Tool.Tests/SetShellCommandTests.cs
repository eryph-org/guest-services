using AwesomeAssertions;
using Eryph.GuestServices.Tool.Commands;

namespace Eryph.GuestServices.Tool.Tests;

public class SetShellCommandTests
{
    [Theory]
    [InlineData("shell-override", "shell-override", true)]
    [InlineData("other shell-override more", "shell-override", true)]
    [InlineData("  shell-override   ", "shell-override", true)]
    [InlineData("a  b  shell-override", "shell-override", true)]
    [InlineData("other", "shell-override", false)]
    [InlineData("", "shell-override", false)]
    [InlineData("   ", "shell-override", false)]
    [InlineData(null, "shell-override", false)]
    [InlineData("Shell-Override", "shell-override", false)]
    [InlineData("shell-override-extra", "shell-override", false)]
    public void HasFeature_ParsesSpaceSeparatedList(string? featureList, string feature, bool expected)
    {
        SetShellCommand.HasFeature(featureList, feature).Should().Be(expected);
    }
}
