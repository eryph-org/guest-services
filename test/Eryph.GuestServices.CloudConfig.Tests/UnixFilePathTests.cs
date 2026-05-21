namespace Eryph.GuestServices.CloudConfig.Tests;

public class UnixFilePathTests
{
    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData("/var/log/messages")]
    [InlineData("/")]
    [InlineData("/a")]
    [InlineData("/home/user/file.txt")]
    public void NewValidation_AbsolutePath_ReturnsSuccess(string path)
    {
        var result = UnixFilePath.NewValidation(path);

        result.ShouldBeSuccess().Value.Should().Be(path);
    }

    [Theory]
    [InlineData("etc/passwd")]
    [InlineData("./file")]
    [InlineData("file.txt")]
    [InlineData("")]
    public void NewValidation_NotAbsolute_ReturnsFail(string path)
    {
        var result = UnixFilePath.NewValidation(path);

        result.ShouldBeFail().Should().NotBeEmpty();
    }

    [Theory]
    [InlineData("/etc/./passwd")]
    [InlineData("/etc/../passwd")]
    [InlineData("/.")]
    [InlineData("/..")]
    public void NewValidation_RelativeSegments_ReturnsFail(string path)
    {
        var result = UnixFilePath.NewValidation(path);

        result.ShouldBeFail().Flatten()
            .Should().Contain(e => e.Message.Contains("relative segments"));
    }

    [Theory]
    [InlineData("/etc\\passwd")]
    [InlineData("/etc/file\0name")]
    public void NewValidation_InvalidCharacters_ReturnsFail(string path)
    {
        var result = UnixFilePath.NewValidation(path);

        result.ShouldBeFail().Flatten()
            .Should().Contain(e => e.Message.Contains("invalid characters"));
    }
}
