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
}
