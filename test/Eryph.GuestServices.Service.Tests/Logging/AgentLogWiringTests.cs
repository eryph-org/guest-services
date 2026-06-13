using AwesomeAssertions;
using Eryph.GuestServices.Core;
using Eryph.GuestServices.Core.Logging;
using Microsoft.Extensions.Logging;

namespace Eryph.GuestServices.Service.Tests.Logging;

// All tests here mutate the shared AgentPaths.RootOverride. They live in one
// class so xunit runs them sequentially (no within-class parallelism).
public sealed class AgentLogWiringTests : IDisposable
{
    private readonly string _root;

    public AgentLogWiringTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "egs-agentpaths-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        AgentPaths.RootOverride = _root;
    }

    public void Dispose()
    {
        AgentPaths.RootOverride = null;
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void LogFile_is_agent_log_under_the_logs_directory()
    {
        AgentPaths.LogsDirectory.Should().Be(Path.Combine(_root, "logs"));
        AgentPaths.LogFile.Should().Be(Path.Combine(_root, "logs", "agent.log"));
    }

    [Fact]
    public void Default_log_path_is_under_the_service_wide_guest_services_root()
    {
        AgentPaths.RootOverride = null;

        // Cross-platform: Windows -> %ProgramData%\eryph\guest-services\logs,
        // Linux -> /var/log/eryph/guest-services. Both carry the service-wide
        // 'guest-services' segment and the file is always agent.log.
        AgentPaths.LogsDirectory.Should().Contain("guest-services");
        AgentPaths.LogFile.Should().EndWith("agent.log");
        // Must NOT live under the provisioning tree — that was the whole point
        // of moving the sink out of the provisioning feature.
        AgentPaths.LogsDirectory.Should().NotContain("provisioning");
    }

    [Fact]
    public void AddProvider_with_AgentPaths_LogFile_writes_the_agent_log()
    {
        // Faithful to the composition roots (Program.cs / RunCommand), which do
        // logging.AddProvider(new FileLoggerProvider(AgentPaths.LogFile)).
        using (var factory = LoggerFactory.Create(builder =>
                   builder.AddProvider(new FileLoggerProvider(AgentPaths.LogFile))))
        {
            factory.CreateLogger("Eryph.Wiring.Test").LogInformation("wired line");
        }

        File.Exists(AgentPaths.LogFile).Should().BeTrue();
        File.ReadAllText(AgentPaths.LogFile).Should().Contain("wired line");
    }
}
