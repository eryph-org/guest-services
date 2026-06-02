using Eryph.GuestServices.Tool.Eryph;
using Eryph.GuestServices.Tool.Interceptors;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Eryph.GuestServices.Tool.Commands.Eryph;

// egs-tool eryph add-ssh-config <catletId> [--identity <path>] [--add-key]
//
// Resolves a single catlet via the compute client and writes one
// "<catlet>.<project>.eryph.alt" SSH alias whose ProxyCommand bridges through
// the eryph channel. Per-catlet and on demand; never enumerates all catlets.
public class EryphAddSshConfigCommand : AsyncCommand<EryphAddSshConfigCommand.Settings>
{
    public class Settings : CommandSettings, IElevationExempt
    {
        [CommandArgument(0, "<CatletId>")]
        public string CatletId { get; set; } = string.Empty;

        // BYOK: select the private key the generated alias uses. Omitted => the
        // managed ClientKeyHelper key.
        [CommandOption("--identity <PATH>")]
        public string? Identity { get; set; }

        // Also run the runtime key-push flow so the managed/identity public key
        // is authorized in the guest right away.
        [CommandOption("--add-key")]
        public bool AddKey { get; set; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        var connection = EryphConnection.Resolve();
        if (connection is null)
        {
            AnsiConsole.MarkupLineInterpolated(
                $"[red]Could not find an eryph connection. Is eryph configured or eryph-zero running?[/]");
            return -1;
        }

        var catletsClient = connection.CreateCatletsClient();
        var catlet = (await catletsClient.GetAsync(settings.CatletId)).Value;
        if (catlet is null)
        {
            AnsiConsole.MarkupLineInterpolated(
                $"[red]The catlet '{settings.CatletId}' could not be found.[/]");
            return -1;
        }

        // --identity selects the operator's own private key (BYOK); otherwise
        // the alias points at the managed client key.
        var keyFilePath = !string.IsNullOrEmpty(settings.Identity)
            ? settings.Identity
            : ClientKeyHelper.PrivateKeyPath;

        await SshConfigHelper.EnsureSshConfigAsync();
        var aliases = await SshConfigHelper.EnsureCatletConfigAsync(
            catlet.Id,
            catlet.Name,
            catlet.Project.Name,
            keyFilePath);

        if (settings.AddKey)
        {
            // Push the managed public key (no --public-key here; --identity is a
            // private-key selector for the alias, not a public key to install).
            var keyResult = await EryphAddKeyCommand.PushKeyAsync(
                connection, catlet.Id, publicKeyOption: null, ttl: null);
            if (keyResult != 0)
                return keyResult;
        }

        AnsiConsole.Write(new Rows(
            new Text("An SSH configuration for the catlet has been generated here:"),
            new Text(SshConfigHelper.CatletSshConfigPath),
            new Text("The configuration has been included in your sshconfig."),
            new Text(""),
            new Text("You can connect to the catlet as follows:"),
            new Padder(new Rows(aliases.Select(a => new Text($"ssh {a}"))), new Padding(4, 0, 0, 0))));

        return 0;
    }
}
