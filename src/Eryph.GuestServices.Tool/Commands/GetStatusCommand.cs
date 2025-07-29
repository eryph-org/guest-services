using Eryph.GuestServices.Core;
using Eryph.GuestServices.HvDataExchange.Host;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Eryph.GuestServices.Tool.Commands;

public class GetStatusCommand : AsyncCommand<GetStatusCommand.Settings>
{
    public class Settings : CommandSettings
    {
        [CommandArgument(0, "<VmId>")] public Guid VmId { get; set; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        var hostDataExchange = new HostDataExchange();
        var guestData = await hostDataExchange.GetGuestDataAsync(settings.VmId);
        guestData.TryGetValue(Constants.StatusKey, out var status);
        AnsiConsole.WriteLine(string.IsNullOrEmpty(status) ? "unknown" : status);
        
        return 0;
    }
}
