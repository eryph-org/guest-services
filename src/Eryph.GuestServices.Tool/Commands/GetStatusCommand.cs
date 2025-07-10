using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using Eryph.GuestServices.Core;
using Eryph.GuestServices.DataExchange;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Eryph.GuestServices.Tool.Commands;

public class GetStatusCommand : Command<GetStatusCommand.Settings>
{
    public class Settings : CommandSettings
    {
        [CommandArgument(0, "<VmId>")] public Guid VmId { get; set; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        var hostDataExchange = new HostDataExchange();
        var guestData = hostDataExchange.GetGuestData(settings.VmId);
        guestData.TryGetValue(Constants.StatusKey, out var status);
        AnsiConsole.WriteLine(string.IsNullOrEmpty(status) ? "unknown" : status);
        
        return 0;
    }
}
