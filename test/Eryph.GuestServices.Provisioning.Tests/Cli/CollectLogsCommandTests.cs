using System.IO.Compression;
using AwesomeAssertions;
using Eryph.GuestServices.Core;
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
        // The agent log lives under a separate (service-wide) root; give it its
        // own subdir so the two logs/ sources stay distinct in the test.
        AgentPaths.RootOverride = Path.Combine(_root, "gs");
        _output = Path.Combine(_root, "bundle.zip");
    }

    public void Dispose()
    {
        ProvisioningPaths.RootOverride = null;
        AgentPaths.RootOverride = null;
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
    public async Task ExecuteAsync_includes_provisioning_script_logs_when_present()
    {
        // The provisioning logs dir holds per-script logs (ScriptsUser etc.).
        var logsDir = Path.Combine(_root, "logs");
        Directory.CreateDirectory(logsDir);
        await File.WriteAllTextAsync(Path.Combine(logsDir, "001-foo.ps1.log"), "hello");
        await File.WriteAllTextAsync(Path.Combine(logsDir, "002-bar.ps1.log"), "world");

        var sut = new CollectLogsCommand();
        var exit = await sut.ExecuteAsync(
            TestCommandContext.Create("collect-logs"),
            new CollectLogsCommand.Settings { Output = _output });

        exit.Should().Be(0);
        using var archive = ZipFile.OpenRead(_output);
        var names = archive.Entries.Select(e => e.FullName).ToArray();
        names.Should().Contain("logs/001-foo.ps1.log");
        names.Should().Contain("logs/002-bar.ps1.log");
    }

    [Fact]
    public async Task ExecuteAsync_includes_the_agent_log_from_the_guest_services_root()
    {
        // The agent's own operational log lives under the service-wide
        // guest-services root, NOT the provisioning root — collect-logs must
        // still bundle it (issue #45). It maps under the same logs/ prefix.
        var agentLogs = AgentPaths.LogsDirectory;
        Directory.CreateDirectory(agentLogs);
        await File.WriteAllTextAsync(Path.Combine(agentLogs, "agent.log"), "agent diag");

        var sut = new CollectLogsCommand();
        var exit = await sut.ExecuteAsync(
            TestCommandContext.Create("collect-logs"),
            new CollectLogsCommand.Settings { Output = _output });

        exit.Should().Be(0);
        using var archive = ZipFile.OpenRead(_output);
        archive.Entries.Select(e => e.FullName).Should().Contain("logs/agent.log");
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
