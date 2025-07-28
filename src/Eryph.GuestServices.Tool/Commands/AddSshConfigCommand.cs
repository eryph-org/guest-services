using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Eryph.GuestServices.Core;
using Eryph.GuestServices.HvDataExchange.Host;
using Microsoft.DevTunnels.Ssh;
using Microsoft.DevTunnels.Ssh.Keys;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Eryph.GuestServices.Tool.Commands;

public class AddSshConfigCommand : AsyncCommand<AddSshConfigCommand.Settings>
{
    public class Settings : CommandSettings
    {
        [CommandArgument(0, "<VmId>")] public Guid VmId { get; set; }

        [CommandArgument(1, "[Alias]")] public string? Alias { get; set; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        var keyPair = await ClientKeyHelper.GetKeyPairAsync();
        if (keyPair is null)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]No SSH key found. Have you run the initialize command?[/]");
            return -1;
        }

        var publicKey = KeyPair.ExportPublicKey(keyPair, keyFormat: KeyFormat.Ssh);
        var hostDataExchange = new HostDataExchange();
        await hostDataExchange.SetExternalDataAsync(
            settings.VmId,
            new Dictionary<string, string?>
            {
                [Constants.ClientAuthKey] = publicKey,
            });

        await SshConfigHelper.EnsureEryphSshConfigAsync();
        await SshConfigHelper.EnsureVmConfigAsync(settings.VmId, settings.Alias, ClientKeyHelper.PrivateKeyPath);

        AnsiConsole.MarkupLineInterpolated($"SSH config added for VM {settings.VmId}. You can connect with:");
        AnsiConsole.MarkupLineInterpolated($"\tssh hyperv/{settings.VmId}");
        if(!string.IsNullOrEmpty(settings.Alias))
            AnsiConsole.MarkupLineInterpolated($"\tssh {settings.Alias}");

        return 0;
    }
}
