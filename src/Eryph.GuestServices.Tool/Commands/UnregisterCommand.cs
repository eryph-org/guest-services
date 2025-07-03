using Eryph.GuestServices.Core;
using Spectre.Console.Cli;

namespace Eryph.GuestServices.Tool.Commands
{
    internal class UnregisterCommand : Command<UnregisterCommand.Settings>
    {
        public class Settings : CommandSettings
        {
        }

        public override int Execute(CommandContext context, Settings settings)
        {
            Registration.Unregister(Constants.ServiceId);
            return 0;
        }
    }
}
