using Eryph.GuestServices.Core;
using Eryph.GuestServices.HvDataExchange.Host;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Eryph.GuestServices.Tool.Commands;

public class SetShellCommand : AsyncCommand<SetShellCommand.Settings>
{
    public class Settings : CommandSettings
    {
        [CommandArgument(0, "<VmId>")] public Guid VmId { get; set; }

        [CommandOption("-c|--command <CMD>")]
        public string? Command { get; set; }

        [CommandOption("-a|--arguments <ARGS>")]
        public string? Arguments { get; set; }

        [CommandOption("--reset")]
        public bool Reset { get; set; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        if (settings.Reset)
        {
            if (!string.IsNullOrWhiteSpace(settings.Command) || !string.IsNullOrWhiteSpace(settings.Arguments))
            {
                AnsiConsole.MarkupLineInterpolated(
                    $"[red]--reset cannot be combined with --command or --arguments.[/]");
                return -1;
            }
        }
        else if (string.IsNullOrWhiteSpace(settings.Command))
        {
            AnsiConsole.MarkupLineInterpolated(
                $"[red]Either --command <CMD> or --reset is required.[/]");
            return -1;
        }

        // Collapse blanks to null so a stray whitespace pad (or an explicit
        // empty value) doesn't end up persisted as dead data in KVP. The
        // selector only honors a non-blank value anyway.
        var command = string.IsNullOrWhiteSpace(settings.Command) ? null : settings.Command.Trim();
        var arguments = string.IsNullOrWhiteSpace(settings.Arguments) ? null : settings.Arguments.Trim();

        var values = new Dictionary<string, string?>
        {
            [Constants.ShellKey] = settings.Reset ? null : command,
            [Constants.ShellArgsKey] = settings.Reset ? null : arguments,
        };

        var hostDataExchange = new HostDataExchange();
        await hostDataExchange.SetExternalValuesAsync(settings.VmId, values);

        if (settings.Reset)
        {
            AnsiConsole.MarkupLineInterpolated(
                $"[green]Cleared the shell override for VM {settings.VmId}.[/]");
        }
        else
        {
            var argsDisplay = string.IsNullOrEmpty(arguments) ? "(none)" : arguments;
            AnsiConsole.Write(new Rows(
                new Markup($"[green]Shell override set for VM {settings.VmId}.[/]"),
                new Text($"  Command:   {command}"),
                new Text($"  Arguments: {argsDisplay}"),
                new Text(""),
                new Text("Takes effect on the next SSH session.")));
        }

        return 0;
    }
}
