using AwesomeAssertions;
using Eryph.GuestServices.CloudConfig.Validation;

namespace Eryph.GuestServices.CloudConfig.Tests;

public sealed class PowerStateGrammarTests
{
    [Theory]
    [InlineData("reboot", PowerStateGrammar.PowerStateMode.Reboot)]
    [InlineData("Reboot", PowerStateGrammar.PowerStateMode.Reboot)]
    [InlineData("REBOOT", PowerStateGrammar.PowerStateMode.Reboot)]
    [InlineData("poweroff", PowerStateGrammar.PowerStateMode.Poweroff)]
    [InlineData("shutdown", PowerStateGrammar.PowerStateMode.Poweroff)]   // cbi alias
    [InlineData("halt", PowerStateGrammar.PowerStateMode.Halt)]
    [InlineData(null, PowerStateGrammar.PowerStateMode.Reboot)]            // default
    [InlineData("", PowerStateGrammar.PowerStateMode.Reboot)]
    public void ParseMode_resolves_documented_values(string? input, PowerStateGrammar.PowerStateMode expected)
    {
        PowerStateGrammar.ParseMode(input).ShouldBeSuccess().Should().Be(expected);
    }

    [Theory]
    [InlineData("klingon")]
    [InlineData("graceful")]
    [InlineData("force-reboot")]
    public void ParseMode_rejects_unknown_values(string input)
    {
        var errors = PowerStateGrammar.ParseMode(input).ShouldBeFail();
        errors.Should().NotBeEmpty();
        errors[0].Message.Should().Contain(input);
    }

    [Theory]
    [InlineData("now", 0)]
    [InlineData("", 0)]
    [InlineData(null, 0)]
    [InlineData("0", 0)]
    [InlineData("30", 30)]
    [InlineData("+1", 60)]
    [InlineData("+5", 300)]
    public void ParseDelay_handles_documented_forms(string? input, int expectedSeconds)
    {
        PowerStateGrammar.ParseDelay(input, DateTimeOffset.UtcNow).ShouldBeSuccess()
            .Should().Be(expectedSeconds);
    }

    [Fact]
    public void ParseDelay_HHMM_in_future_is_seconds_until_today()
    {
        // 10:00 UTC reference; "15:30" should be 5h30m later (in local zone).
        var now = new DateTimeOffset(2026, 5, 23, 10, 0, 0, TimeSpan.Zero);
        var seconds = PowerStateGrammar.ParseDelay("15:30", now).ShouldBeSuccess();
        // Allow for the test running in a non-UTC local zone: the parse
        // converts `now` to local time first, so the difference still
        // resolves to (15:30 local - now local).
        seconds.Should().BeGreaterThan(0);
        seconds.Should().BeLessThan(48 * 3600);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("+abc")]
    [InlineData("99:99")]
    [InlineData("-30")]
    public void ParseDelay_rejects_malformed_input(string input)
    {
        var errors = PowerStateGrammar.ParseDelay(input, DateTimeOffset.UtcNow).ShouldBeFail();
        errors.Should().NotBeEmpty();
        errors[0].Message.Should().Contain(input);
    }

    [Fact]
    public void Validate_aggregates_mode_and_delay_errors()
    {
        // Pin that both validation failures aggregate (via the `|`
        // operator) rather than the first short-circuiting the second.
        // This is the key value of LanguageExt's Validation over plain
        // Either / Result types.
        var cfg = new PowerStateConfig { Mode = "klingon", Delay = "abc" };
        var errors = PowerStateGrammar.Validate(cfg).ShouldBeFail().Flatten();
        errors.Should().HaveCountGreaterThanOrEqualTo(2);
        errors.Select(e => e.Message).Should().Contain(m => m.Contains("klingon"));
        errors.Select(e => e.Message).Should().Contain(m => m.Contains("abc"));
    }

    [Fact]
    public void Validate_accepts_message_within_512_char_Windows_limit()
    {
        var cfg = new PowerStateConfig { Message = new string('x', 512) };
        PowerStateGrammar.Validate(cfg).ShouldBeSuccess();
    }

    [Fact]
    public void Validate_rejects_message_over_512_chars()
    {
        var cfg = new PowerStateConfig { Message = new string('x', 513) };
        var errors = PowerStateGrammar.Validate(cfg).ShouldBeFail();
        errors.Should().NotBeEmpty();
    }
}
