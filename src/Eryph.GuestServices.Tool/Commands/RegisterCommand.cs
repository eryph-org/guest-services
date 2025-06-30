using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Eryph.GuestServices.Core;
using Spectre.Console.Cli;

namespace Eryph.GuestServices.Tool.Commands;

internal class RegisterCommand : Command<RegisterCommand.Settings>
{
    public class Settings : CommandSettings
    {
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        Registration.Register(Constants.ServiceId, "eryph guest services");
        return 0;
    }
}
