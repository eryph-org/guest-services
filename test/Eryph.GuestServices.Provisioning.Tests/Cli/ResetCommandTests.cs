using AwesomeAssertions;
using Eryph.GuestServices.Provisioning.Cli;

namespace Eryph.GuestServices.Provisioning.Tests.Cli;

/// <summary>
/// The ResetCommand deletes well-known paths under <c>%ProgramData%\eryph\provisioning</c>.
/// Tests redirect that root to a temp directory via <see cref="ProvisioningPaths.RootOverride"/>
/// so we never touch the real install.
/// </summary>
[Collection(nameof(ProvisioningPathsCollection))]
public sealed class ResetCommandTests : IDisposable
{
    private readonly string _root;

    public ResetCommandTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "egs-reset-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        ProvisioningPaths.RootOverride = _root;
    }

    public void Dispose()
    {
        ProvisioningPaths.RootOverride = null;
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task ExecuteAsync_no_state_file_returns_zero()
    {
        var sut = new ResetCommand();

        var exit = await sut.ExecuteAsync(
            TestCommandContext.Create("reset"),
            new ResetCommand.Settings { Yes = true });

        exit.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_deletes_existing_state_file()
    {
        var stateFile = Path.Combine(_root, "state.json");
        await File.WriteAllTextAsync(stateFile, "{}");

        var sut = new ResetCommand();
        var exit = await sut.ExecuteAsync(
            TestCommandContext.Create("reset"),
            new ResetCommand.Settings { Yes = true });

        exit.Should().Be(0);
        File.Exists(stateFile).Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_with_logs_deletes_logs_directory()
    {
        var logsDir = Path.Combine(_root, "logs");
        Directory.CreateDirectory(logsDir);
        await File.WriteAllTextAsync(Path.Combine(logsDir, "agent.log"), "hello");

        var sut = new ResetCommand();
        var exit = await sut.ExecuteAsync(
            TestCommandContext.Create("reset"),
            new ResetCommand.Settings { Yes = true, ClearLogs = true });

        exit.Should().Be(0);
        Directory.Exists(logsDir).Should().BeFalse();
    }
}
