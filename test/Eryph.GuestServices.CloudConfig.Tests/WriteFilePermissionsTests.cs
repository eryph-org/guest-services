namespace Eryph.GuestServices.CloudConfig.Tests;

public class WriteFilePermissionsTests
{
    [Theory]
    [InlineData("0644", "0644")]
    [InlineData("644", "0644")]
    [InlineData("0o644", "0644")]
    [InlineData("0O644", "0644")]
    [InlineData("0755", "0755")]
    [InlineData("0", "0000")]
    [InlineData("7777", "7777")]
    [InlineData("0777", "0777")]
    public void NewValidation_ValidOctal_ReturnsSuccessNormalized(string input, string expected)
    {
        var result = WriteFilePermissions.NewValidation(input);

        result.ShouldBeSuccess().Value.Should().Be(expected);
    }

    [Theory]
    [InlineData("0888")]
    [InlineData("abc")]
    [InlineData("0x644")]
    [InlineData("-1")]
    [InlineData("0999")]
    [InlineData("9")]
    public void NewValidation_NonOctal_ReturnsFail(string input)
    {
        var result = WriteFilePermissions.NewValidation(input);

        result.ShouldBeFail().Flatten()
            .Should().Contain(e => e.Message.Contains("octal"));
    }

    [Theory]
    [InlineData("06444")]
    [InlineData("12345")]
    public void NewValidation_TooManyDigits_ReturnsFail(string input)
    {
        var result = WriteFilePermissions.NewValidation(input);

        result.ShouldBeFail().Flatten()
            .Should().Contain(e => e.Message.Contains("four octal digits"));
    }

    [Fact]
    public void NewValidation_Empty_ReturnsFail()
    {
        var result = WriteFilePermissions.NewValidation("");

        result.ShouldBeFail().Should().NotBeEmpty();
    }
}
