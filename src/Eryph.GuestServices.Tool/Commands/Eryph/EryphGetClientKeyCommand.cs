using Eryph.GuestServices.Tool.Interceptors;
using Microsoft.DevTunnels.Ssh;
using Microsoft.DevTunnels.Ssh.Algorithms;
using Microsoft.DevTunnels.Ssh.Keys;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Eryph.GuestServices.Tool.Commands.Eryph;

// egs-tool catlet get-client-key
//
// Prints the managed client PUBLIC key in OpenSSH authorized_keys form, for
// pasting into a catlet spec / fodder to pre-inject (the build-time flow).
public class EryphGetClientKeyCommand : AsyncCommand<EryphGetClientKeyCommand.Settings>
{
    public class Settings : CommandSettings, IElevationExempt;

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        IKeyPair keyPair;
        try
        {
            // The per-user managed key, created on demand so there is always a key
            // to print for pre-injection.
            keyPair = await UserClientKeyHelper.EnsureKeyPairAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AnsiConsole.MarkupLine(
                $"[red]The managed client key could not be created: {ex.Message.EscapeMarkup()}[/]");
            return -1;
        }

        var publicKey = KeyPair.ExportPublicKey(keyPair, keyFormat: KeyFormat.Ssh);

        // Do not use AnsiConsole here as it would introduce line breaks into the output.
        Console.Write(publicKey);

        return 0;
    }
}
