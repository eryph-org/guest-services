using Eryph.GuestServices.Tool.Interceptors;
using Microsoft.DevTunnels.Ssh;
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
        var keyPair = await ClientKeyHelper.GetKeyPairAsync();
        if (keyPair is null)
        {
            AnsiConsole.MarkupLineInterpolated(
                $"[red]No client key found. Run 'egs-tool eryph add-ssh-config' or 'initialize' first.[/]");
            return -1;
        }

        var publicKey = KeyPair.ExportPublicKey(keyPair, keyFormat: KeyFormat.Ssh);

        // Do not use AnsiConsole here as it would introduce line breaks into the output.
        Console.Write(publicKey);

        return 0;
    }
}
