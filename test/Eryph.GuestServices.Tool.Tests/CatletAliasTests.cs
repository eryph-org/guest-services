using AwesomeAssertions;
using Eryph.GuestServices.Tool;

namespace Eryph.GuestServices.Tool.Tests;

public class CatletAliasTests
{
    [Fact]
    public void GetCatletAliases_NonDefaultProject_ProducesIdAndQualifiedNameAlias()
    {
        var aliases = SshConfigHelper.GetCatletAliases(
            "abcd-1234", "web", "production");

        aliases.Should().Equal(
            "abcd-1234.eryph.alt",
            "web.production.eryph.alt");
    }

    [Fact]
    public void GetCatletAliases_DefaultProject_AlsoProducesShortNameAlias()
    {
        var aliases = SshConfigHelper.GetCatletAliases(
            "abcd-1234", "web", "default");

        aliases.Should().Equal(
            "abcd-1234.eryph.alt",
            "web.default.eryph.alt",
            "web.eryph.alt");
    }

    [Fact]
    public void GetCatletAliases_AllAliasesUseReservedEryphSuffix()
    {
        var aliases = SshConfigHelper.GetCatletAliases(
            "abcd-1234", "web", "default");

        aliases.Should().AllSatisfy(a => SshConfigHelper.IsReservedAlias(a).Should().BeTrue());
    }

    [Theory]
    // A name carrying whitespace/comment/glob characters cannot be a safe
    // ssh_config Host token, so only the canonical catletId alias is emitted.
    [InlineData("web server", "default")]
    [InlineData("web", "proj#1")]
    [InlineData("web*", "default")]
    [InlineData("", "default")]
    public void GetCatletAliases_UnsafeNameComponents_OnlyEmitsCanonicalAlias(
        string catletName, string projectName)
    {
        var aliases = SshConfigHelper.GetCatletAliases(
            "abcd-1234", catletName, projectName);

        aliases.Should().Equal("abcd-1234.eryph.alt");
    }
}
