using System.Text;
using Eryph.ClientRuntime.Configuration;
using Eryph.ComputeClient;
using Eryph.ComputeClient.Models;
using Eryph.IdentityModel.Clients;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Eryph.GuestServices.Tool.Commands;

public class UpdateSshConfigCommand : AsyncCommand<UpdateSshConfigCommand.Settings>
{
    public class Settings : CommandSettings
    {
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    { 
        var catletsClient = CreateClient();
        if (catletsClient is null)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Cannot connect to eryph. Is eryph-zero running?[/red]");
            // TODO define better error code
            return -1;
        }

        var catlets = new List<Catlet>();
        await foreach (var catlet in catletsClient.ListAsync())
        {
            catlets.Add(catlet);
        }

        await SshConfigHelper.EnsureSshConfigAsync();
        await SshConfigHelper.CleanupCatletConfigsAsync(catlets.Select(c => c.Id).ToList());

        var table = new Table();
        table.AddColumn("Project");
        table.AddColumn("Catlet");
        table.AddColumn("SSH command");

        foreach (var catlet in catlets)
        {
            var aliases = await SshConfigHelper.EnsureCatletConfigAsync(
                catlet.Id,
                catlet.Name,
                catlet.Project.Name,
                Guid.Parse(catlet.VmId),
                ClientKeyHelper.PrivateKeyPath);

            table.AddRow(
                new Text(catlet.Project.Name),
                new Text(catlet.Name),
                new Rows(aliases.Select(a => new Text($"ssh {a}"))));
        }

        AnsiConsole.Write(new Rows(
            new Text("SSH configurations for your catlets have been generated here:"),
            new Text(SshConfigHelper.CatletSshConfigPath),
            new Text("The configurations have been included in your sshconfig."),
            new Text(""),
            new Text("You can connect to the catlets as follows:"),
            table));

        return 0;
    }

    private CatletsClient? CreateClient()
    {
        var configuration = ConfigurationNames.Zero;
        var environment = new DefaultEnvironment();
        var credentialsLookup = new ClientCredentialsLookup(environment);
        var credentials = credentialsLookup.GetSystemClientCredentials(configuration);
        if (credentials is null)
            return null;

        var endpointLookup = new EndpointLookup(environment);
        var endpoint = endpointLookup.GetEndpoint("compute", configuration);

        var clientsFactory = new ComputeClientsFactory(
            new EryphComputeClientOptions(credentials), endpoint);

        return clientsFactory.CreateCatletsClient();
    }
}
