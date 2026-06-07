using Eryph.ComputeClient.Models;
using Eryph.GuestServices.Tool.Eryph;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Eryph.GuestServices.Tool.Commands.Eryph;

// egs-tool catlet get-status <catletId>
//
// Reads the catlet guest's services state (agent status/version, provisioning
// state, shell) via the compute client's operation and prints it.
public class EryphGetStatusCommand : AsyncCommand<EryphGetStatusCommand.Settings>
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

        string operationId;
        try
        {
            operationId = (await connection.CreateCatletsClient(EryphConnection.CatletsReadScope)
                .GetGuestServicesAsync(settings.CatletId)).Value.Id;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine(
                $"[red]Failed to read the guest services status: {ex.Message.EscapeMarkup()}[/]");
            return -1;
        }

        var operation = await EryphOperations.WaitForCompletionAsync(
            connection.CreateOperationsClient(), operationId);
        if (operation is null)
        {
            AnsiConsole.MarkupLine("[red]Timed out waiting for the guest services status.[/]");
            return -1;
        }

        if (operation.Status == OperationStatus.Failed)
        {
            AnsiConsole.MarkupLine("[red]The guest services status could not be read.[/]");
            return -1;
        }

        if (operation.Result is not GuestServicesStatusOperationResult result)
        {
            AnsiConsole.MarkupLine("[red]The operation did not return a guest services status.[/]");
            return -1;
        }

        AnsiConsole.MarkupLine($"Guest services: {(result.GuestServicesStatus ?? "unknown").EscapeMarkup()}");
        AnsiConsole.MarkupLine($"Version:        {(result.GuestServicesVersion ?? "-").EscapeMarkup()}");
        AnsiConsole.MarkupLine($"Provisioning:   {(result.ProvisioningState ?? "-").EscapeMarkup()}");
        AnsiConsole.MarkupLine($"Shell:          {(result.Shell ?? "-").EscapeMarkup()}");
        return 0;
    }
}
