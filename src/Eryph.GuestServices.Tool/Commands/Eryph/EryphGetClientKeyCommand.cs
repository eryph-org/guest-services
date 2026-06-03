using Eryph.GuestServices.Tool.Interceptors;
using Microsoft.DevTunnels.Ssh;
using Microsoft.DevTunnels.Ssh.Algorithms;
using Microsoft.DevTunnels.Ssh.Keys;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Eryph.GuestServices.Tool.Commands.Eryph;

// egs-tool eryph get-client-key
//
// Prints the managed client PUBLIC key in OpenSSH authorized_keys form, for
// pasting into a catlet spec / fodder to pre-inject (the build-time flow).
public class EryphGetClientKeyCommand : AsyncCommand<EryphGetClientKeyCommand.Settings>
{
    public class Settings : CommandSettings, IElevationExempt;

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        IKeyPair? keyPair;
        try
        {
            keyPair = await ClientKeyHelper.GetKeyPairAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AnsiConsole.MarkupLineInterpolated(
                $"[red]The managed client key could not be read: {ex.Message}[/]");
            return -1;
        }

        if (keyPair is null)
        {
            // Only 'initialize' creates the managed key (add-ssh-config does not).
            AnsiConsole.MarkupLineInterpolated(
                $"[red]No managed client key found. Run 'egs-tool initialize' first.[/]");
            return -1;
        }

        var publicKey = KeyPair.ExportPublicKey(keyPair, keyFormat: KeyFormat.Ssh);

        // Do not use AnsiConsole here as it would introduce line breaks into the output.
        Console.Write(publicKey);

        return 0;
    }
}
