using Eryph.GuestServices.Core;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Eryph.GuestServices.Tool.Commands;

internal class InitializeCommand : AsyncCommand<InitializeCommand.Settings>
{
    public class Settings : CommandSettings
    {
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        Registration.Register(Constants.ServiceId, Constants.ServiceName);

        var clientKey = await ClientKeyHelper.GetKeyPairAsync();
        if (clientKey is not null)
        {
            AnsiConsole.WriteLine("The SSH key already exists.");
            return 0;
        }

        await ClientKeyHelper.CreateKeyPairAsync();
        return 0;
    }
}
