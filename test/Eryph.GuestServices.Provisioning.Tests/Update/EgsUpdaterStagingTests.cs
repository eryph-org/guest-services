using AwesomeAssertions;
using Eryph.GuestServices.Core;
using Eryph.GuestServices.Provisioning.Update;

namespace Eryph.GuestServices.Provisioning.Tests.Update;

public sealed class EgsUpdaterStagingTests
{
    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("")]
    public void StagingDirFor_rejects_traversal_tokens(string version)
    {
        // A pinned `version` comes straight from user-data; "." / ".." would make
        // the later Directory.Delete escape the update root and wipe service
        // state/logs.
        var act = () => EgsUpdater.StagingDirFor(version);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void StagingDirFor_neutralises_separators_and_stays_under_update_root()
    {
        // Embedded separators are invalid filename chars -> replaced, so even a
        // crafted value resolves to a direct child of the update directory.
        var root = Path.GetFullPath(AgentPaths.UpdateDirectory);

        var staged = EgsUpdater.StagingDirFor("../../etc/passwd");

        Path.GetFullPath(staged).Should().StartWith(root);
        Path.GetDirectoryName(staged).Should().Be(root.TrimEnd(Path.DirectorySeparatorChar));
    }

    [Fact]
    public void StagingDirFor_keeps_a_normal_version_verbatim()
    {
        var staged = EgsUpdater.StagingDirFor("0.4.0");
        Path.GetFileName(staged).Should().Be("0.4.0");
    }
}
