using AwesomeAssertions;
using Eryph.GuestServices.CloudConfig.Yaml.Converters;

namespace Eryph.GuestServices.CloudConfig.Yaml.Tests;

/// <summary>
/// Direct unit coverage for the YAML 1.1 / PyYAML SafeLoader implicit
/// integer grammar. Pins the forms cloud-init's <c>yaml.safe_load</c>
/// accepts — leading-zero octal, underscore separators, binary, hex — and
/// the deliberate sexagesimal carve-out.
/// </summary>
public sealed class Yaml11IntegerTokensTests
{
    [Theory]
    // Leading-zero octal (the key YAML 1.1 fix). PyYAML reads `0644` as 420,
    // not decimal 644.
    [InlineData("0644", 420)]
    [InlineData("017", 15)]
    [InlineData("-0644", -420)]
    // Bare zero / decimal.
    [InlineData("0", 0)]
    [InlineData("+5", 5)]
    [InlineData("42", 42)]
    [InlineData("-42", -42)]
    // Underscore digit separators.
    [InlineData("1_000", 1000)]
    [InlineData("1_000_000", 1000000)]
    // Binary.
    [InlineData("0b101", 5)]
    [InlineData("0B101", 5)]
    // Hex.
    [InlineData("0x1F", 31)]
    [InlineData("0X1f", 31)]
    // YAML 1.2 explicit octal prefix — accepted for forward-compat.
    [InlineData("0o17", 15)]
    [InlineData("0O17", 15)]
    public void TryParse_resolves_YAML11_integer_forms(string text, long expected)
    {
        Yaml11IntegerTokens.TryParse(text, out var value).Should().BeTrue($"'{text}' is a YAML 1.1 integer");
        value.Should().Be(expected);
    }

    [Theory]
    // Sexagesimal — PyYAML resolves `12:30` to 750, but we deliberately do
    // NOT, to avoid corrupting unquoted times-of-day / ratios. Must fall
    // through to string.
    [InlineData("12:30")]
    [InlineData("1:30:00")]
    // Double / leading / trailing underscore — not valid separators.
    [InlineData("1__0")]
    [InlineData("_10")]
    [InlineData("10_")]
    // Non-integers.
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("1.5")]
    [InlineData("0x")]      // prefix with no digits
    [InlineData("0b")]
    [InlineData("0b102")]   // 2 is not a binary digit
    [InlineData("0xZZ")]    // not hex digits
    [InlineData("+")]       // sign only
    [InlineData("-")]
    public void TryParse_returns_false_for_non_integers(string text)
    {
        Yaml11IntegerTokens.TryParse(text, out _).Should().BeFalse($"'{text}' is not a YAML 1.1 integer");
    }
}
