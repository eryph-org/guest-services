namespace Eryph.GuestServices.CloudConfig.Tests;

public class WindowsUserNameTests
{
    [Theory]
    [InlineData("administrator")]
    [InlineData("Administrator")]
    [InlineData("user1")]
    [InlineData("ADMIN")]
    [InlineData("a")]
    [InlineData("svc.admin")]
    [InlineData("user-with-dash")]
    [InlineData("user_underscore")]
    [InlineData("user with space")]
    public void NewValidation_ValidName_ReturnsSuccessLowercase(string name)
    {
        var result = WindowsUserName.NewValidation(name);

        var value = result.ShouldBeSuccess();
        value.Value.Should().Be(name.ToLowerInvariant());
    }

    [Theory]
    [InlineData("ab/cd")]
    [InlineData("ab\\cd")]
    [InlineData("ab[cd")]
    [InlineData("ab]cd")]
    [InlineData("ab:cd")]
    [InlineData("ab;cd")]
    [InlineData("ab|cd")]
    [InlineData("ab=cd")]
    [InlineData("ab,cd")]
    [InlineData("ab+cd")]
    [InlineData("ab*cd")]
    [InlineData("ab?cd")]
    [InlineData("ab<cd")]
    [InlineData("ab>cd")]
    [InlineData("ab\"cd")]
    public void NewValidation_InvalidCharacters_ReturnsFail(string name)
    {
        var result = WindowsUserName.NewValidation(name);

        var errors = result.ShouldBeFail().Flatten();
        errors.Should().ContainSingle(e => e.Message.Contains("invalid characters"));
    }

    [Fact]
    public void NewValidation_TooLong_ReturnsFail()
    {
        var name = new string('a', 21);

        var result = WindowsUserName.NewValidation(name);

        var errors = result.ShouldBeFail().Flatten();
        errors.Should().Contain(e => e.Message.Contains("longer than"));
    }

    [Fact]
    public void NewValidation_Empty_ReturnsFail()
    {
        var result = WindowsUserName.NewValidation("");

        result.ShouldBeFail().Should().NotBeEmpty();
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("...")]
    [InlineData(" . . ")]
    public void NewValidation_AllDotsOrSpaces_ReturnsFail(string name)
    {
        var result = WindowsUserName.NewValidation(name);

        var errors = result.ShouldBeFail().Flatten();
        errors.Should().Contain(e => e.Message.Contains("dots and spaces"));
    }

    [Fact]
    public void Equality_DifferentCase_AreEqual()
    {
        var a = new WindowsUserName("Admin");
        var b = new WindowsUserName("ADMIN");

        a.Should().Be(b);
    }
}
