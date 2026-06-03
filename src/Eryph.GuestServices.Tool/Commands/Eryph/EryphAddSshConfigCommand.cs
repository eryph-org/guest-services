using Eryph.ComputeClient.Models;
using Eryph.GuestServices.Tool.Eryph;
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
    public class Settings : EryphConnectionSettings
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
        var connection = EryphConnection.Resolve(settings.ClientId, settings.Configuration);
        if (connection is null)
        {
            AnsiConsole.MarkupLineInterpolated(
                $"[red]Could not find an eryph connection. Is eryph configured or eryph-zero running?[/]");
            return -1;
        }

        var catletsClient = connection.CreateCatletsClient(EryphConnection.CatletsReadScope);
        Catlet? catlet;
        try
        {
            catlet = (await catletsClient.GetAsync(settings.CatletId)).Value;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine(
                $"[red]The catlet '{settings.CatletId.EscapeMarkup()}' could not be retrieved: {ex.Message.EscapeMarkup()}[/]");
            return -1;
        }

        if (catlet is null)
        {
            AnsiConsole.MarkupLine(
                $"[red]The catlet '{settings.CatletId.EscapeMarkup()}' could not be found.[/]");
            return -1;
        }

        // --identity selects the operator's own private key (BYOK); otherwise
        // the alias points at the managed client key. Validate it up front: an
        // alias whose IdentityFile does not exist fails later inside ssh with a
        // non-obvious error.
        if (!string.IsNullOrEmpty(settings.Identity) && !File.Exists(settings.Identity))
        {
            AnsiConsole.MarkupLine(
                $"[red]The identity file '{settings.Identity.EscapeMarkup()}' does not exist.[/]");
            return -1;
        }

        string keyFilePath;
        if (!string.IsNullOrEmpty(settings.Identity))
        {
            keyFilePath = settings.Identity;
        }
        else
        {
            // No --identity: use the per-user managed key, creating it on demand.
            // It lives in the operator's profile with a user-only ACL, so Windows
            // OpenSSH will load it (unlike the admin/SYSTEM-scoped service key).
            try
            {
                await UserClientKeyHelper.EnsureKeyPairAsync();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AnsiConsole.MarkupLine(
                    $"[red]The managed client key could not be created: {ex.Message.EscapeMarkup()}[/]");
                return -1;
            }

            keyFilePath = UserClientKeyHelper.PrivateKeyPath;
        }

        await SshConfigHelper.EnsureSshConfigAsync();
        // Persist the connection selectors into the generated ProxyCommand: the
        // alias is used non-interactively by ssh later, so the proxy must resolve
        // the same client/configuration the operator selected here.
        var aliases = await SshConfigHelper.EnsureCatletConfigAsync(
            catlet.Id,
            catlet.Name,
            catlet.Project.Name,
            keyFilePath,
            settings.ClientId,
            settings.Configuration);

        if (settings.AddKey)
        {
            // Authorize the key the generated alias will actually present. With
            // --identity (BYOK) that is the identity's public half (<identity>.pub
            // by ssh convention); otherwise it is the managed client key. Pushing
            // the managed key while the alias uses a different --identity key would
            // leave the guest authorizing a key the operator never presents.
            string? addKeyPublicKey = null;
            if (!string.IsNullOrEmpty(settings.Identity))
            {
                addKeyPublicKey = settings.Identity + ".pub";
                if (!File.Exists(addKeyPublicKey))
                {
                    AnsiConsole.MarkupLine(
                        $"[red]Cannot push the key: the public key '{addKeyPublicKey.EscapeMarkup()}' was not found next to the identity file.[/]");
                    return -1;
                }
            }

            var keyResult = await EryphAddKeyCommand.PushKeyAsync(
                connection, catlet.Id, addKeyPublicKey, ttl: null);
            if (keyResult != 0)
                return keyResult;
        }

        AnsiConsole.Write(new Rows(
            new Text("An SSH configuration for the catlet has been generated here:"),
            new Text(SshConfigHelper.CatletSshConfigPath),
            new Text("The configuration has been included in your SSH config."),
            new Text(""),
            new Text("You can connect to the catlet as follows:"),
            new Padder(new Rows(aliases.Select(a => new Text($"ssh {a}"))), new Padding(4, 0, 0, 0))));

        return 0;
    }
}
