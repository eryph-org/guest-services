using Eryph.GuestServices.Tool.Eryph;
using Eryph.GuestServices.Tool.Interceptors;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Eryph.GuestServices.Tool.Commands.Eryph;

// egs-tool eryph remove-key <catletId>
//
// Revokes the caller's own key: DELETE the key-install route. The server
// derives the subject from the bearer token, so no key is sent.
public class EryphRemoveKeyCommand : AsyncCommand<EryphRemoveKeyCommand.Settings>
{
    public class Settings : CommandSettings, IElevationExempt
    {
        [CommandArgument(0, "<CatletId>")]
        public string CatletId { get; set; } = string.Empty;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        var connection = EryphConnection.Resolve();
        if (connection is null)
        {
            AnsiConsole.MarkupLineInterpolated(
                $"[red]Could not find an eryph connection. Is eryph configured or eryph-zero running?[/]");
            return -1;
        }

        using var httpClient = new HttpClient();
        var token = await connection.GetAccessTokenAsync();
        httpClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var uri = connection.BuildComputeUri($"catlets/{settings.CatletId}/ssh-keys");
        var response = await httpClient.DeleteAsync(uri);
        if (!response.IsSuccessStatusCode)
        {
            AnsiConsole.MarkupLineInterpolated(
                $"[red]Failed to remove the key ({(int)response.StatusCode} {response.ReasonPhrase}).[/]");
            return -1;
        }

        AnsiConsole.MarkupLineInterpolated($"[green]The key was removed.[/]");
        return 0;
    }
}
