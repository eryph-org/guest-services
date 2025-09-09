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

public class UploadDirectoryCommand : AsyncCommand<UploadDirectoryCommand.Settings>
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

        if (!Directory.Exists(settings.SourcePath))
        {
            AnsiConsole.MarkupLineInterpolated($"[red]The directory '{settings.SourcePath}' does not exist.[/]");
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
            
            if (!isAuthenticated)
            {
                AnsiConsole.MarkupLineInterpolated($"[red]Could not connect. The authentication failed.[/]");
                return -1;
            }

            return await UploadDirectoryAsync(clientSession, settings);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Error: {ex.Message}[/]");
            return -1;
        }
    }

    private static async Task<int> UploadDirectoryAsync(SshSession session, Settings settings)
    {
        return await session.UploadDirectoryAsync(
            settings.SourcePath,
            settings.TargetPath,
            settings.Overwrite,
            settings.Recursive,
            writeInfo: msg => AnsiConsole.MarkupLineInterpolated($"[blue]{msg}[/]"),
            writeError: msg => AnsiConsole.MarkupLineInterpolated($"[red]{msg}[/]"),
            writeWarning: msg => AnsiConsole.MarkupLineInterpolated($"[yellow]{msg}[/]"),
            writeSuccess: msg => AnsiConsole.MarkupLineInterpolated($"[green]{msg}[/]"));
    }
}