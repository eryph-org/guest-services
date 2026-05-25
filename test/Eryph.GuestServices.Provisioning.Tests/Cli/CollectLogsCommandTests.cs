using System.IO.Compression;
using AwesomeAssertions;
using Eryph.GuestServices.Provisioning.Cli;

namespace Eryph.GuestServices.Provisioning.Tests.Cli;

[Collection(nameof(ProvisioningPathsCollection))]
public sealed class CollectLogsCommandTests : IDisposable
{
    private readonly string _root;
    private readonly string _output;

    public CollectLogsCommandTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "egs-collect-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        ProvisioningPaths.RootOverride = _root;
        _output = Path.Combine(_root, "bundle.zip");
    }

    public void Dispose()
    {
        ProvisioningPaths.RootOverride = null;
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task ExecuteAsync_produces_zip_when_no_inputs_exist()
    {
        var sut = new CollectLogsCommand();

        var exit = await sut.ExecuteAsync(
            TestCommandContext.Create("collect-logs"),
            new CollectLogsCommand.Settings { Output = _output });

        exit.Should().Be(0);
        File.Exists(_output).Should().BeTrue();

        // Even with no inputs we still include version.txt so the bundle has
        // a deterministic shape for triage tooling.
        using var archive = ZipFile.OpenRead(_output);
        archive.Entries.Select(e => e.FullName).Should().Contain("version.txt");
    }

    [Fact]
    public async Task ExecuteAsync_includes_state_file_when_present()
    {
        var stateFile = Path.Combine(_root, "state.json");
        await File.WriteAllTextAsync(stateFile, "{\"instanceId\":\"x\"}");

        var sut = new CollectLogsCommand();
        var exit = await sut.ExecuteAsync(
            TestCommandContext.Create("collect-logs"),
            new CollectLogsCommand.Settings { Output = _output });

        exit.Should().Be(0);
        using var archive = ZipFile.OpenRead(_output);
        archive.Entries.Select(e => e.FullName).Should().Contain("state.json");
    }

    [Fact]
    public async Task ExecuteAsync_includes_logs_directory_when_present()
    {
        var logsDir = Path.Combine(_root, "logs");
        Directory.CreateDirectory(logsDir);
        await File.WriteAllTextAsync(Path.Combine(logsDir, "agent.log"), "hello");
        await File.WriteAllTextAsync(Path.Combine(logsDir, "trace.log"), "world");

        var sut = new CollectLogsCommand();
        var exit = await sut.ExecuteAsync(
            TestCommandContext.Create("collect-logs"),
            new CollectLogsCommand.Settings { Output = _output });

        exit.Should().Be(0);
        using var archive = ZipFile.OpenRead(_output);
        var names = archive.Entries.Select(e => e.FullName).ToArray();
        names.Should().Contain("logs/agent.log");
        names.Should().Contain("logs/trace.log");
    }

    [Fact]
    public async Task ExecuteAsync_returns_two_when_output_missing()
    {
        var sut = new CollectLogsCommand();

        var exit = await sut.ExecuteAsync(
            TestCommandContext.Create("collect-logs"),
            new CollectLogsCommand.Settings { Output = "" });

        exit.Should().Be(2);
    }
}
