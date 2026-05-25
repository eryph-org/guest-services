using AwesomeAssertions;
using Eryph.GuestServices.CloudConfig.Validation;

namespace Eryph.GuestServices.CloudConfig.Tests;

public sealed class GrowpartGrammarTests
{
    [Theory]
    [InlineData("/", GrowpartGrammar.DeviceKind.SystemDrive, null)]
    [InlineData("\\", GrowpartGrammar.DeviceKind.SystemDrive, null)]
    [InlineData("all", GrowpartGrammar.DeviceKind.All, null)]
    [InlineData("ALL", GrowpartGrammar.DeviceKind.All, null)]
    [InlineData("C", GrowpartGrammar.DeviceKind.DriveLetter, 'C')]
    [InlineData("c", GrowpartGrammar.DeviceKind.DriveLetter, 'C')]
    [InlineData("C:", GrowpartGrammar.DeviceKind.DriveLetter, 'C')]
    [InlineData("D:\\", GrowpartGrammar.DeviceKind.DriveLetter, 'D')]
    [InlineData("E:/", GrowpartGrammar.DeviceKind.DriveLetter, 'E')]
    public void ParseDevice_resolves_documented_shapes(
        string input, GrowpartGrammar.DeviceKind expectedKind, char? expectedLetter)
    {
        var target = GrowpartGrammar.ParseDevice(input).ShouldBeSuccess();
        target.Kind.Should().Be(expectedKind);
        target.DriveLetter.Should().Be(expectedLetter);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("1")]
    [InlineData("/var")]
    [InlineData("C:foo")]
    [InlineData("C:\\path")]
    public void ParseDevice_rejects_malformed_input(string input)
    {
        var errors = GrowpartGrammar.ParseDevice(input).ShouldBeFail();
        errors.Should().NotBeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("auto")]
    [InlineData("Auto")]
    [InlineData("off")]
    [InlineData("OFF")]
    [InlineData("false")]
    [InlineData("False")]
    public void Validate_accepts_documented_modes(string? mode)
    {
        var cfg = new GrowpartConfig { Mode = mode };
        GrowpartGrammar.Validate(cfg).ShouldBeSuccess();
    }

    [Theory]
    [InlineData("growpart")]
    [InlineData("gpart")]
    [InlineData("yes")]
    public void Validate_rejects_unsupported_modes(string mode)
    {
        var cfg = new GrowpartConfig { Mode = mode };
        var errors = GrowpartGrammar.Validate(cfg).ShouldBeFail();
        errors.Should().NotBeEmpty();
        errors[0].Message.Should().Contain(mode);
    }

    [Fact]
    public void Validate_aggregates_per_index_device_errors()
    {
        // Per-index prefix means the operator can see which entry is bad.
        // Pin that two bad entries surface as two distinct error messages.
        var cfg = new GrowpartConfig
        {
            Mode = "auto",
            Devices = ["/", "garbage", "C:", "also-bad"],
        };
        var errors = GrowpartGrammar.Validate(cfg).ShouldBeFail().Flatten();
        errors.Should().HaveCountGreaterThanOrEqualTo(2);
        errors.Select(e => e.Message).Should().Contain(m => m.Contains("devices[1]"));
        errors.Select(e => e.Message).Should().Contain(m => m.Contains("devices[3]"));
    }

    [Fact]
    public void Validate_accepts_empty_devices_list()
    {
        var cfg = new GrowpartConfig { Devices = [] };
        GrowpartGrammar.Validate(cfg).ShouldBeSuccess();
    }
}
