using Eryph.GuestServices.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

namespace Eryph.GuestServices.Provisioning.Logging;

/// <summary>
/// Configures all agent logging through Serilog, driven entirely by the single
/// <c>Serilog</c> section of <c>appsettings.json</c> (level, file rolling/size/
/// retention, console, and — on Windows — the Event Log). Serilog owns the whole
/// pipeline, so there is one place to tune logging.
/// <para>
/// The Event Log sink is Windows-only (it throws on Linux), so it lives in an
/// <c>appsettings.windows.json</c> overlay loaded only on Windows. The log file
/// path is environment-derived (<see cref="AgentPaths.LogFile"/>) and injected
/// here rather than baked into appsettings.
/// </para>
/// </summary>
public static class AgentLogging
{
    public static void AddAgentLogging(this IHostApplicationBuilder builder)
    {
        // Windows-only sink overlay (Event Log) merged onto the shared config.
        if (OperatingSystem.IsWindows())
        {
            builder.Configuration.AddJsonFile(
                Path.Combine(AppContext.BaseDirectory, "appsettings.windows.json"),
                optional: true,
                reloadOnChange: false);
        }

        // The OS-specific log path is injected so the same appsettings works on
        // Windows and Linux.
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Serilog:WriteTo:agentFile:Args:path"] = AgentPaths.LogFile,
        });

        // Create the log directory eagerly so collect-logs has a directory to
        // harvest even on a guest that never logged a line (the file sink creates
        // it lazily on first write). Best-effort.
        try
        {
            Directory.CreateDirectory(AgentPaths.LogsDirectory);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        // Serilog becomes the whole logging pipeline: drop the default
        // Microsoft.Extensions.Logging providers and route every event through
        // Serilog's sinks, configured from the Serilog section.
        builder.Logging.ClearProviders();
        builder.Services.AddSerilog((services, loggerConfiguration) => loggerConfiguration
            .ReadFrom.Configuration(builder.Configuration)
            .ReadFrom.Services(services));
    }
}
