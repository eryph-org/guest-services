namespace Eryph.GuestServices.CloudConfig.Tests;

public class LinuxUserNameTests
{
    [Theory]
    [InlineData("root")]
    [InlineData("admin")]
    [InlineData("_svc")]
    [InlineData("u1")]
    [InlineData("user-name")]
    [InlineData("user_name")]
    [InlineData("a")]
    public void NewValidation_ValidName_ReturnsSuccess(string name)
    {
        var result = LinuxUserName.NewValidation(name);

        result.ShouldBeSuccess().Value.Should().Be(name);
    }

    [Theory]
    [InlineData("Root", "lowercase letter")]
    [InlineData("ADMIN", "lowercase letter")]
    [InlineData("0user", "lowercase letter")]
    [InlineData("-user", "lowercase letter")]
    [InlineData("user with space", "lowercase letter")]
    [InlineData("user.name", "lowercase letter")]
    [InlineData("user/name", "lowercase letter")]
    public void NewValidation_InvalidName_ReturnsFail(string name, string expectedFragment)
    {
        var result = LinuxUserName.NewValidation(name);

        result.ShouldBeFail().Flatten()
            .Should().Contain(e => e.Message.Contains(expectedFragment));
    }

    [Fact]
    public void NewValidation_TooLong_ReturnsFail()
    {
        var name = "a" + new string('b', 32);

        var result = LinuxUserName.NewValidation(name);

        result.ShouldBeFail().Flatten()
            .Should().Contain(e => e.Message.Contains("longer than"));
    }

    [Fact]
    public void NewValidation_Empty_ReturnsFail()
    {
        var result = LinuxUserName.NewValidation("");

        result.ShouldBeFail().Should().NotBeEmpty();
    }
}
