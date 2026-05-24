using AwesomeAssertions;
using Eryph.GuestServices.CloudConfig.Yaml.Converters;

namespace Eryph.GuestServices.CloudConfig.Yaml.Tests;

/// <summary>
/// Direct unit coverage for the YAML 1.1 / PyYAML SafeLoader bool token
/// table. Pins the exact 22-token set cloud-init relies on — any drift
/// breaks parity with <c>yaml.safe_load</c>.
/// </summary>
public sealed class Yaml11BoolTokensTests
{
    [Theory]
    [InlineData("true", true)]
    [InlineData("True", true)]
    [InlineData("TRUE", true)]
    [InlineData("false", false)]
    [InlineData("False", false)]
    [InlineData("FALSE", false)]
    [InlineData("yes", true)]
    [InlineData("Yes", true)]
    [InlineData("YES", true)]
    [InlineData("no", false)]
    [InlineData("No", false)]
    [InlineData("NO", false)]
    [InlineData("on", true)]
    [InlineData("On", true)]
    [InlineData("ON", true)]
    [InlineData("off", false)]
    [InlineData("Off", false)]
    [InlineData("OFF", false)]
    [InlineData("y", true)]
    [InlineData("Y", true)]
    [InlineData("n", false)]
    [InlineData("N", false)]
    public void TryParse_recognises_all_22_PyYAML_SafeLoader_bool_tokens(string text, bool expected)
    {
        Yaml11BoolTokens.TryParse(text, out var value).Should().BeTrue($"'{text}' is a YAML 1.1 bool token");
        value.Should().Be(expected);
        Yaml11BoolTokens.IsBoolToken(text).Should().BeTrue();
    }

    [Theory]
    [InlineData("YES ")]      // trailing whitespace — PyYAML strips during scalar parse, the token itself is exact-case
    [InlineData(" yes")]      // leading whitespace
    [InlineData("Yess")]      // off-by-one
    [InlineData("yEs")]       // mixed case beyond the 22 listed forms
    [InlineData("truefalse")] // concatenation
    [InlineData("0")]         // YAML 1.1 reserves digits for ints, not bools
    [InlineData("1")]
    [InlineData("True!")]
    [InlineData("")]          // empty string is not a bool token
    [InlineData(" ")]         // bare whitespace
    [InlineData("maybe")]
    public void TryParse_rejects_non_token_strings(string text)
    {
        Yaml11BoolTokens.TryParse(text, out _).Should().BeFalse($"'{text}' is not a YAML 1.1 bool token");
        Yaml11BoolTokens.IsBoolToken(text).Should().BeFalse();
    }
}
