using Eryph.GuestServices.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

namespace Eryph.GuestServices.Provisioning.Logging;

/// <summary>
/// Wires the agent's file log via Serilog, configured from the <c>Serilog</c>
/// section of <c>appsettings.json</c> (level, rolling, size cap, retention) so
/// the housekeeping is operator-tunable instead of hard-coded. The log file
/// path itself is environment-derived (<see cref="AgentPaths.LogFile"/>) rather
/// than baked into appsettings, so it is injected here.
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

        // Create the log directory eagerly so collect-logs has a directory to
        // harvest even on a guest that never logged a line (Serilog's file sink
        // creates it lazily on first write). Best-effort.
        try
        {
            Directory.CreateDirectory(AgentPaths.LogsDirectory);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        var fileLogger = new LoggerConfiguration()
            .ReadFrom.Configuration(builder.Configuration)
            .CreateLogger();

        // Add Serilog as an ADDITIONAL logging provider that owns only the file
        // sink. The ILoggingBuilder overload keeps the default
        // Microsoft.Extensions.Logging pipeline intact, so the Windows Event Log
        // (AddWindowsService), the systemd journal and the console still receive
        // events. The IServiceCollection AddSerilog overload would instead
        // replace the ILoggerFactory and silence those providers.
        builder.Logging.AddSerilog(fileLogger, dispose: true);
    }
}
