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
        // Dual-write for cross-version compatibility:
        //   - Named slot (new shape): re-running on the same host is
        //     idempotent; running on a different host adds to the set without
        //     evicting prior entries. Read by guest services >= this version.
        //   - Legacy single slot: still consulted by older egs-service guests
        //     that don't know about the named-slot family. Writing it keeps
        //     this command working against pre-upgrade catlets. Multi-host
        //     callers still race for the legacy slot (last-writer-wins) on
        //     old guests, but that's the pre-existing behaviour, not a
        //     regression introduced here. The legacy write can be dropped
        //     once we no longer support upgrading from pre-multi-key guests.
        var slotKey = Constants.ClientAuthKeyPrefix + Environment.MachineName.ToLowerInvariant();
        await hostDataExchange.SetExternalValuesAsync(
            settings.VmId,
            new Dictionary<string, string?>
            {
                [Constants.ClientAuthKey] = publicKey,
                [slotKey] = publicKey,
            });

        await SshConfigHelper.EnsureSshConfigAsync();
        var aliases = await SshConfigHelper.EnsureVmConfigAsync(settings.VmId, settings.Alias, ClientKeyHelper.PrivateKeyPath);

        AnsiConsole.Write(new Rows(
            new Text("An SSH configuration for the virtual machine has been generated here:"),
            new Text(SshConfigHelper.VmSshConfigPath),
            new Text("The configuration has been included in your sshconfig."),
            new Text(""),
            new Text("You can connect to the virtual machine as follows:"),
            new Padder(new Rows(aliases.Select(a => new Text($"ssh {a}"))), new Padding(4, 0, 0, 0))));

        return 0;
    }
}
