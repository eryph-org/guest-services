using Microsoft.DevTunnels.Ssh;
using Microsoft.DevTunnels.Ssh.Keys;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Eryph.GuestServices.Tool.Commands;

public class GetSshKeyCommand : Command<GetSshKeyCommand.Settings>
{
    public class Settings : CommandSettings
    {
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        var keyFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "eryph",
            "guest-services",
            "private",
            "id_egs");
        
        if (!Path.Exists(keyFilePath))
        {
            AnsiConsole.MarkupLineInterpolated($"[red]No SSH key found. Have you run the initialize command?[red]");
            return -1;
        }

        var keyPair = KeyPair.ImportKeyFile(keyFilePath);
        var publicKey = KeyPair.ExportPublicKey(keyPair, keyFormat: KeyFormat.Ssh);

        AnsiConsole.WriteLine(publicKey);

        return 0;
    }
}
