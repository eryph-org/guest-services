using System.ComponentModel;
using Eryph.GuestServices.Provisioning.Configuration;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Eryph.GuestServices.Provisioning.Cli;

/// <summary>
/// Clears persistent provisioning state so the next agent run treats the
/// guest as a fresh instance. <c>--logs</c> and <c>--scripts</c> also wipe
/// the corresponding directories under <c>%ProgramData%\eryph\provisioning</c>.
/// </summary>
public sealed class ResetCommand : AsyncCommand<ResetCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--logs")]
        [Description("Also delete the agent logs directory.")]
        public bool ClearLogs { get; init; }

        [CommandOption("--scripts")]
        [Description("Also delete the staged scripts directory.")]
        public bool ClearScripts { get; init; }

        [CommandOption("-y|--yes")]
        [Description("Skip the confirmation prompt (CI / automation).")]
        public bool Yes { get; init; }
    }

    public override Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        var items = new List<string> { ProvisioningPaths.StateFile };
        if (settings.ClearLogs) items.Add(ProvisioningPaths.LogsDirectory);
        if (settings.ClearScripts)
            items.Add(ProvisioningPaths.ScriptsDirectory(ProvisioningSettings.LoadOrDefault()));

        AnsiConsole.MarkupLine("[yellow]The following paths will be removed:[/]");
        foreach (var p in items)
            AnsiConsole.MarkupLineInterpolated($"  {p}");

        if (!settings.Yes)
        {
            if (!AnsiConsole.Confirm("Continue?", defaultValue: false))
            {
                AnsiConsole.MarkupLine("[grey]Aborted.[/]");
                return Task.FromResult(0);
            }
        }

        var removed = 0;
        foreach (var path in items)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                    removed++;
                    AnsiConsole.MarkupLineInterpolated($"[green]Deleted file:[/] {path}");
                }
                else if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                    removed++;
                    AnsiConsole.MarkupLineInterpolated($"[green]Deleted directory:[/] {path}");
                }
                else
                {
                    AnsiConsole.MarkupLineInterpolated($"[grey]Not present:[/] {path}");
                }
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLineInterpolated($"[red]Failed to delete {path}: {ex.Message}[/]");
                return Task.FromResult(1);
            }
        }

        AnsiConsole.MarkupLineInterpolated($"[green]Removed {removed} item(s).[/]");
        return Task.FromResult(0);
    }
}
