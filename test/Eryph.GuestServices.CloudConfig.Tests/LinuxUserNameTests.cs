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
    [InlineData("Root")]
    [InlineData("ADMIN")]
    [InlineData("0user")]
    [InlineData("-user")]
    [InlineData("user with space")]
    [InlineData("user.name")]
    [InlineData("user/name")]
    public void NewValidation_InvalidName_ReturnsFail(string name)
    {
        var result = LinuxUserName.NewValidation(name);

        result.ShouldBeFail().Should().NotBeEmpty();
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
