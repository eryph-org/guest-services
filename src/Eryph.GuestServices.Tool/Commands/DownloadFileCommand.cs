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

public class DownloadFileCommand : AsyncCommand<DownloadFileCommand.Settings>
{
    public class Settings : CommandSettings
    {
        [CommandArgument(0, "<VmId>")] public Guid VmId { get; set; }

        [CommandArgument(1, "<SourcePath>")] public string SourcePath { get; set; } = string.Empty;

        [CommandArgument(2, "<TargetPath>")] public string TargetPath { get; set; } = string.Empty;

        [CommandOption("--overwrite")] public bool Overwrite { get; set; }
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

        await clientSession.ConnectAsync(clientStream);
        var isAuthenticated = await clientSession.AuthenticateAsync(new SshClientCredentials("egs", keyPair));
        if (!isAuthenticated)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Could not connect. The authentication failed.[/]");
            return -1;
        }

        // Download the specified file
        return await DownloadFileAsync(clientSession, settings);
    }

    private async Task<int> DownloadFileAsync(SshSession session, Settings settings, CancellationToken cancellationToken = default)
    {
        // Check if target already exists and overwrite is not set
        if (File.Exists(settings.TargetPath) && !settings.Overwrite)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]The file '{settings.TargetPath}' already exists.[/]");
            return ErrorCodes.FileExists;
        }

        // Create target directory if it doesn't exist
        var targetDirectory = Path.GetDirectoryName(settings.TargetPath);
        if (!string.IsNullOrEmpty(targetDirectory) && !Directory.Exists(targetDirectory))
        {
            Directory.CreateDirectory(targetDirectory);
        }

        try
        {
            AnsiConsole.MarkupLineInterpolated($"[blue]Downloading file '{settings.SourcePath}'...[/]");
            
            await using var targetStream = new FileStream(settings.TargetPath, FileMode.Create, FileAccess.Write);
            var result = await session.DownloadFileAsync(settings.SourcePath, "", targetStream, cancellationToken);

            if (result == 0)
            {
                AnsiConsole.MarkupLineInterpolated($"[green]File downloaded successfully to '{settings.TargetPath}'[/]");
                return 0;
            }

            if (result == ErrorCodes.FileNotFound)
            {
                AnsiConsole.MarkupLineInterpolated($"[red]The file '{settings.SourcePath}' was not found on the VM.[/]");
            }
            else
            {
                AnsiConsole.MarkupLineInterpolated($"[red]Download failed with error code: {result}[/]");
            }

            // Clean up the partial file
            if (File.Exists(settings.TargetPath))
            {
                File.Delete(settings.TargetPath);
            }
            return result;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Download failed: {ex.Message}[/]");
            
            // Clean up the partial file
            if (File.Exists(settings.TargetPath))
            {
                File.Delete(settings.TargetPath);
            }
            return -1;
        }
    }
}