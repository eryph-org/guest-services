using AwesomeAssertions;
using Eryph.GuestServices.Tool.Commands.Eryph;

namespace Eryph.GuestServices.Tool.Tests;

public class EryphConnectionSettingsTests
{
    [Fact]
    public void Validate_NoSelectors_Succeeds()
    {
        new EryphConnectionSettings().Validate().Successful.Should().BeTrue();
    }

    [Theory]
    [InlineData("default")]
    [InlineData("zero")]
    [InlineData("33d5b71e-a84a-4792-bb97-09f121b5ecc9")]
    [InlineData("my_client.1")]
    public void Validate_SafeSelectors_Succeeds(string value)
    {
        new EryphConnectionSettings { Configuration = value, ClientId = value }
            .Validate().Successful.Should().BeTrue();
    }

    [Theory]
    // A value that could break out of the quoted ProxyCommand argument or inject
    // shell tokens must be rejected.
    [InlineData("a b")]
    [InlineData("a\"b")]
    [InlineData("a\\b")]
    [InlineData("a&b")]
    [InlineData("")]
    public void Validate_UnsafeConfiguration_Fails(string value)
    {
        new EryphConnectionSettings { Configuration = value }.Validate().Successful.Should().BeFalse();
    }

    [Theory]
    [InlineData("a b")]
    [InlineData("a\"b")]
    [InlineData("a\\b")]
    public void Validate_UnsafeClientId_Fails(string value)
    {
        new EryphConnectionSettings { ClientId = value }.Validate().Successful.Should().BeFalse();
    }
}
