using AwesomeAssertions;
using Eryph.GuestServices.Tool.Eryph;

namespace Eryph.GuestServices.Tool.Tests;

public class DurationParserTests
{
    [Theory]
    [InlineData("8h", 0, 8, 0, 0)]
    [InlineData("30m", 0, 0, 30, 0)]
    [InlineData("90s", 0, 0, 1, 30)]
    [InlineData("2d", 2, 0, 0, 0)]
    [InlineData("1d12h30m", 1, 12, 30, 0)]
    [InlineData("  8H ", 0, 8, 0, 0)]
    public void TryParse_ValidDurations_ReturnsExpectedTimeSpan(
        string input, int days, int hours, int minutes, int seconds)
    {
        DurationParser.TryParse(input, out var result).Should().BeTrue();
        result.Should().Be(new TimeSpan(days, hours, minutes, seconds));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    // A unit-less number is rejected: silently guessing seconds/minutes/hours
    // would set the wrong expiry.
    [InlineData("8")]
    [InlineData("abc")]
    [InlineData("8x")]
    [InlineData("8h30")]
    // Zero duration is not a valid TTL.
    [InlineData("0s")]
    [InlineData("0h0m")]
    // An out-of-range component must fail gracefully, not throw: int.Parse
    // overflow ("99999999999d") and TimeSpan overflow ("1000000000d") are both
    // just invalid durations.
    [InlineData("99999999999d")]
    [InlineData("1000000000d")]
    public void TryParse_InvalidDurations_ReturnsFalse(string? input)
    {
        DurationParser.TryParse(input, out var result).Should().BeFalse();
        result.Should().Be(TimeSpan.Zero);
    }
}
