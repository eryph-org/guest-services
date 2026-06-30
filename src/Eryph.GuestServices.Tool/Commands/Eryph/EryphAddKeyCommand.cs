using Eryph.GuestServices.Client;
using Eryph.GuestServices.Tool.Eryph;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Eryph.GuestServices.Tool.Commands.Eryph;

// egs-tool catlet add-key <catletId> [--public-key <path|->] [--ttl <duration>]
//
// Pushes a public key to the catlet's guest via eryph (the runtime flow). The
// key is read from --public-key (a path, or "-" for stdin) or the managed key
// when omitted, and sent through the typed compute client.
public class EryphAddKeyCommand : AsyncCommand<EryphAddKeyCommand.Settings>
{
    public class Settings : EryphConnectionSettings
    {
        [CommandArgument(0, "<CatletId>")]
        public string CatletId { get; set; } = string.Empty;

        // A path to a public key, "-" to read it from stdin, or omitted to use
        // the managed client key.
        [CommandOption("--public-key <PATH>")]
        public string? PublicKey { get; set; }

        // Optional time-to-live, e.g. "8h", "30m", "1d12h". Omitted => the
        // server applies its default / no expiry.
        [CommandOption("--ttl <DURATION>")]
        public string? Ttl { get; set; }
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

        return await PushKeyAsync(connection, settings.CatletId, settings.PublicKey, settings.Ttl);
    }

    // Shared by add-key and add-ssh-config --add-key. Resolves the public key,
    // parses the TTL into an absolute expiry, and installs it via the compute client.
    public static async Task<int> PushKeyAsync(
        EryphConnection connection,
        string catletId,
        string? publicKeyOption,
        string? ttl)
    {
        var publicKey = await PublicKeyReader.ResolveAsync(publicKeyOption);
        if (string.IsNullOrEmpty(publicKey))
        {
            AnsiConsole.MarkupLineInterpolated(
                $"[red]Could not read a public key. Provide --public-key or run 'initialize' for a managed key.[/]");
            return -1;
        }

        TimeSpan? parsedTtl = null;
        if (!string.IsNullOrEmpty(ttl))
        {
            if (!DurationParser.TryParse(ttl, out var parsed))
            {
                AnsiConsole.MarkupLine(
                    $"[red]Invalid --ttl '{ttl.EscapeMarkup()}'. Use a duration such as 8h, 30m or 1d12h.[/]");
                return -1;
            }

            parsedTtl = parsed;
        }

        DateTimeOffset? expiresAt;
        try
        {
            expiresAt = await GuestAccessKey.AddAsync(connection, catletId, publicKey, parsedTtl);
        }
        catch (GuestConnectionException ex)
        {
            AnsiConsole.MarkupLine($"[red]{ex.Message.EscapeMarkup()}[/]");
            return -1;
        }

        if (expiresAt is { } e)
            AnsiConsole.MarkupLine(
                $"[green]The key was added and expires at {e.UtcDateTime.ToString("u").EscapeMarkup()}.[/]");
        else
            AnsiConsole.MarkupLine("[green]The key was added.[/]");

        return 0;
    }
}
