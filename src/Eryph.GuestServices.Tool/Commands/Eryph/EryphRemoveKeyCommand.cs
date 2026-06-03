using Eryph.GuestServices.Tool.Eryph;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Eryph.GuestServices.Tool.Commands.Eryph;

// egs-tool eryph remove-key <catletId>
//
// Revokes the caller's own key via the compute client. The server derives the
// subject from the bearer token, so no key is sent.
public class EryphRemoveKeyCommand : AsyncCommand<EryphRemoveKeyCommand.Settings>
{
    public class Settings : EryphConnectionSettings
    {
        [CommandArgument(0, "<CatletId>")]
        public string CatletId { get; set; } = string.Empty;
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

        try
        {
            await connection.CreateCatletsClient(EryphConnection.RemoteAccessScope)
                .RemoveSshKeyAsync(settings.CatletId);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Failed to remove the key: {ex.Message}[/]");
            return -1;
        }

        AnsiConsole.MarkupLineInterpolated($"[green]The key was removed.[/]");
        return 0;
    }
}
