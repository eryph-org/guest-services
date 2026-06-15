using AwesomeAssertions;
using Eryph.GuestServices.Core;
using Eryph.GuestServices.Provisioning.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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
    public void AddAgentFileLogging_writes_the_agent_log_and_keeps_other_providers()
    {
        // Faithful to the composition roots (Program.cs / RunCommand), which call
        // builder.AddAgentFileLogging(). The Serilog File sink (size/rolling/
        // retention) is configured from the Serilog section; supply a minimal one
        // here. The helper injects AgentPaths.LogFile as the path.
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Serilog:MinimumLevel:Default"] = "Information",
            ["Serilog:WriteTo:agentFile:Name"] = "File",
            ["Serilog:WriteTo:agentFile:Args:rollOnFileSizeLimit"] = "true",
            ["Serilog:WriteTo:agentFile:Args:fileSizeLimitBytes"] = "1048576",
            ["Serilog:WriteTo:agentFile:Args:retainedFileCountLimit"] = "3",
            ["Serilog:WriteTo:agentFile:Args:shared"] = "true",
        });

        // Stand-in for the Event Log / systemd journal / console providers.
        // Serilog must be ADDITIVE: adding the file sink must NOT replace the
        // logging pipeline and silence the other providers.
        var captured = new List<string>();
        builder.Logging.AddProvider(new CapturingLoggerProvider(captured));

        builder.AddAgentFileLogging();

        var host = builder.Build();
        host.Services.GetRequiredService<ILogger<AgentLogWiringTests>>()
            .LogInformation("wired line");
        // Dispose flushes and closes the Serilog file sink.
        host.Dispose();

        // Serilog's file sink captured the line ...
        File.Exists(AgentPaths.LogFile).Should().BeTrue();
        File.ReadAllText(AgentPaths.LogFile).Should().Contain("wired line");
        // ... and the other provider still received it (Serilog did not take
        // over the ILoggerFactory).
        captured.Should().Contain(line => line.Contains("wired line"));
    }

    private sealed class CapturingLoggerProvider(List<string> sink) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(sink);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(List<string> sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) => sink.Add(formatter(state, exception));
        }
    }
}
