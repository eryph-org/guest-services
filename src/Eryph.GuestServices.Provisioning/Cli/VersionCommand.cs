using System.Reflection;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Eryph.GuestServices.Provisioning.Cli;

/// <summary>
/// Prints the agent assembly version (file + informational). Useful for
/// debug reports and for the host-side tooling to confirm what's running.
/// </summary>
public sealed class VersionCommand : Command
{
    public override int Execute(CommandContext context)
    {
        // Report the entry-assembly version (egs-service.exe) rather than this
        // library's: the operator typically wants to know which guest binary is
        // installed, not which provisioning library version it embeds.
        var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        var name = assembly.GetName();
        var info = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var label = name.Name ?? "eryph-guest";
        AnsiConsole.MarkupLineInterpolated($"{label} [bold]{name.Version}[/]");
        if (!string.IsNullOrEmpty(info))
            AnsiConsole.MarkupLineInterpolated($"informational: {info}");
        return 0;
    }
}
