using System.Diagnostics.CodeAnalysis;
using System.Runtime.Versioning;
using AwesomeAssertions;
using Eryph.GuestServices.Provisioning.Cli;
using Eryph.GuestServices.Provisioning.Stages;

namespace Eryph.GuestServices.Provisioning.Tests.Cli;

/// <summary>
/// The RunCommand has two paths (one-shot and Windows-service hosted) which
/// both build a real DI graph and a host. We exercise the surface that can
/// be tested without spinning up the host: option-to-value parsing and the
/// stage name parser.
/// </summary>
[SupportedOSPlatform("windows")]
[RequiresUnreferencedCode("Tests RunCommand which uses ProvisioningContainerBuilder.")]
public sealed class RunCommandTests
{
    [Theory]
    [InlineData("local", Stage.Local)]
    [InlineData("Local", Stage.Local)]
    [InlineData("network", Stage.Network)]
    [InlineData("config", Stage.Config)]
    [InlineData("final", Stage.Final)]
    [InlineData(" Final ", Stage.Final)]
    public void TryParseStage_accepts_known_stage_names(string input, Stage expected)
    {
        RunCommand.TryParseStage(input, out var result).Should().BeTrue();
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("Discovery")]
    [InlineData("")]
    public void TryParseStage_rejects_unknown_stage_names(string input)
    {
        RunCommand.TryParseStage(input, out _).Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_returns_two_for_unknown_stage_argument()
    {
        var sut = new RunCommand();

        var exit = await sut.ExecuteAsync(
            TestCommandContext.Create("run"),
            new RunCommand.Settings { Stage = "unknown" });

        exit.Should().Be(2);
    }

    [Fact]
    public async Task ExecuteAsync_returns_two_when_user_data_file_missing()
    {
        var sut = new RunCommand();
        var missing = Path.Combine(Path.GetTempPath(), "egs-not-real-" + Guid.NewGuid().ToString("N"));

        var exit = await sut.ExecuteAsync(
            TestCommandContext.Create("run"),
            new RunCommand.Settings { UserDataPath = missing });

        exit.Should().Be(2);
    }
}
