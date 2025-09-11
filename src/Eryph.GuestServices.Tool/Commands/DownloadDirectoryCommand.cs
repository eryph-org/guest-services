using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Claims;
using Eryph.GuestServices.Core;
using Eryph.GuestServices.DevTunnels.Ssh.Extensions;
using Eryph.GuestServices.Sockets;
using Microsoft.DevTunnels.Ssh;
using Microsoft.DevTunnels.Ssh.Events;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Eryph.GuestServices.Tool.Commands;

public class DownloadDirectoryCommand : AsyncCommand<DownloadDirectoryCommand.Settings>
{
    public class Settings : CommandSettings
    {
        [CommandArgument(0, "<VmId>")] public Guid VmId { get; set; }

        [CommandArgument(1, "<SourcePath>")] public string SourcePath { get; set; } = string.Empty;

        [CommandArgument(2, "<TargetPath>")] public string TargetPath { get; set; } = string.Empty;

        [CommandOption("--overwrite")] public bool Overwrite { get; set; }
        
        [CommandOption("--recursive")] public bool Recursive { get; set; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        var keyPair = await ClientKeyHelper.GetKeyPairAsync();
        if (keyPair is null)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]No SSH key found. Have you run the initialize command?[/]");
            return -1;
        }
        
        var clientSocket = await SocketFactory.CreateClientSocket(settings.VmId, Constants.ServiceId);
        await using var clientStream = new NetworkStream(clientSocket, true);

        var sshConfig = new SshSessionConfiguration();
        var clientSession = new SshClientSession(sshConfig, new TraceSource("Client"));
        clientSession.Authenticating += (_, e) =>
        {
            if (e.AuthenticationType == SshAuthenticationType.ServerPublicKey)
            {
                // We just trust the host as we connect via the Hyper-V socket.
                e.AuthenticationTask = Task.FromResult<ClaimsPrincipal?>(new ClaimsPrincipal());
            }
        };

        try
        {
            await clientSession.ConnectAsync(clientStream);

            var isAuthenticated = await clientSession.AuthenticateAsync(new SshClientCredentials("egs", keyPair));

            if (isAuthenticated)
            {
                return await clientSession.DownloadDirectoryAsync(
                    settings.SourcePath,
                    settings.TargetPath,
                    settings.Overwrite,
                    settings.Recursive,
                    writeInfo: msg => AnsiConsole.MarkupLineInterpolated($"[cyan]{msg}[/]"),
                    writeError: msg => AnsiConsole.MarkupLineInterpolated($"[red]{msg}[/]"),
                    writeWarning: msg => AnsiConsole.MarkupLineInterpolated($"[orange3]{msg}[/]"),
                    writeSuccess: msg => AnsiConsole.MarkupLineInterpolated($"[green]{msg}[/]"));
            }

            AnsiConsole.MarkupLineInterpolated($"[red]Failed to authenticate to the guest service.[/]");
            return -1;

        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Error: {ex.Message}[/]");
            return -1;
        }
    }

}