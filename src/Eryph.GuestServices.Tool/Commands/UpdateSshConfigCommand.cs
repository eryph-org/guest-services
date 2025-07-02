using Eryph.ClientRuntime.Configuration;
using Eryph.ComputeClient;
using Eryph.ComputeClient.Models;
using Eryph.IdentityModel.Clients;
using Spectre.Console.Cli;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using Spectre.Console;

namespace Eryph.GuestServices.Tool.Commands;

public class UpdateSshConfigCommand : AsyncCommand<UpdateSshConfigCommand.Settings>
{


    public class Settings : CommandSettings
    {
        // Add any settings you need here
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        var sshConfigPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ssh",
            "config");

        var eryphSshConfigPath = Path.Combine(
            // The config should not be in local (non-roaming) profile as the connection
            // only works on the specific machine.
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            ".eryph",
            "ssh",
            "config");

        var include = $"Include {eryphSshConfigPath}{Environment.NewLine}";
        if (!File.Exists(sshConfigPath))
        {
            await File.WriteAllTextAsync(sshConfigPath, include);
        }
        else
        {
            var sshConfig = await File.ReadAllTextAsync(sshConfigPath);
            if (!sshConfig.Contains(include))
            {
                await File.WriteAllTextAsync(sshConfigPath, $"{include}{Environment.NewLine}{sshConfig}");
            }
        }

        var identityFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "eryph",
            "guest-services",
            "private",
            "id_egs");


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
        
        StringBuilder builder = new StringBuilder();
        foreach (var catlet in catlets)
        {
            builder.AppendLine($"Host eryph-{catlet.Name}");
            builder.AppendLine($"    HostName vm-{catlet.VmId}");
            builder.AppendLine($"    User egs");
            builder.AppendLine($"    IdentityFile {identityFilePath}");
            builder.AppendLine($"    ProxyCommand  hvc nc -t vsock {catlet.VmId} 5002");
            //builder.AppendLine($"    ProxyCommand egs-tool.exe proxy {catlet.VmId}");
            builder.AppendLine("");
        }

        if (!Directory.Exists(Path.GetDirectoryName(eryphSshConfigPath)))
            Directory.CreateDirectory(Path.GetDirectoryName(eryphSshConfigPath)!);

        await File.WriteAllTextAsync(eryphSshConfigPath, builder.ToString());

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
