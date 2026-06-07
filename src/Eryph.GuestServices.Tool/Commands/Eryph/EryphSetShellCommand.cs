using Eryph.ComputeClient.Models;
using Eryph.GuestServices.Tool.Eryph;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Eryph.GuestServices.Tool.Commands.Eryph;

// egs-tool catlet set-shell <catletId> <shell> [--args <args>]
//
// Sets the shell the guest's SSH server spawns for interactive sessions, via the
// guest-services settings. Pass an empty shell to clear the override.
public class EryphSetShellCommand : AsyncCommand<EryphSetShellCommand.Settings>
{
    public class Settings : EryphConnectionSettings
    {
        [CommandArgument(0, "<CatletId>")]
        public string CatletId { get; set; } = string.Empty;

        [CommandArgument(1, "<Shell>")]
        public string Shell { get; set; } = string.Empty;

        [CommandOption("--args <ARGS>")]
        public string? Args { get; set; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        var connection = EryphConnection.Resolve(settings.ClientId, settings.Configuration);
        if (connection is null)
        {
            AnsiConsole.MarkupLineInterpolated(
                $"[red]Could not find an eryph connection. Is eryph configured or eryph-zero running?[/]");
            return -1;
        }

        var body = new GuestServicesSettingsBody
        {
            Shell = settings.Shell,
            ShellArgs = settings.Args,
        };

        string operationId;
        try
        {
            operationId = (await connection.CreateCatletsClient(EryphConnection.RemoteAccessScope)
                .SetGuestServicesSettingsAsync(settings.CatletId, body)).Value.Id;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Failed to set the shell: {ex.Message.EscapeMarkup()}[/]");
            return -1;
        }

        var operation = await EryphOperations.WaitForCompletionAsync(
            connection.CreateOperationsClient(), operationId);
        if (operation is null)
        {
            AnsiConsole.MarkupLine("[red]Timed out waiting for the shell to be set.[/]");
            return -1;
        }

        if (operation.Status == OperationStatus.Failed)
        {
            AnsiConsole.MarkupLine("[red]The shell could not be set.[/]");
            return -1;
        }

        AnsiConsole.MarkupLine("[green]The shell was set.[/]");
        return 0;
    }
}
