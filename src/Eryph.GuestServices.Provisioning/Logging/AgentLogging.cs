using Eryph.GuestServices.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace Eryph.GuestServices.Provisioning.Logging;

/// <summary>
/// Wires the agent's file log via Serilog, configured from the <c>Serilog</c>
/// section of <c>appsettings.json</c> (level, rolling, size cap, retention) so
/// the housekeeping is operator-tunable instead of hard-coded. The log file
/// path itself is environment-derived (<see cref="AgentPaths.LogFile"/>) rather
/// than baked into appsettings, so it is injected here.
/// <para>
/// Serilog is added as an additional logging provider; it owns only the file
/// sink. The other sinks (Windows Event Log via <c>AddWindowsService</c>, the
/// systemd journal, console) stay on the default Microsoft.Extensions.Logging
/// pipeline and keep their own <c>Logging</c> configuration.
/// </para>
/// </summary>
public static class AgentLogging
{
    public static void AddAgentFileLogging(this IHostApplicationBuilder builder)
    {
        // appsettings carries only the housekeeping knobs; the OS-specific path
        // is injected here so the same appsettings works on Windows and Linux.
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Serilog:WriteTo:agentFile:Args:path"] = AgentPaths.LogFile,
        });

        builder.Services.AddSerilog((services, loggerConfiguration) => loggerConfiguration
            .ReadFrom.Configuration(builder.Configuration)
            .ReadFrom.Services(services));
    }
}
