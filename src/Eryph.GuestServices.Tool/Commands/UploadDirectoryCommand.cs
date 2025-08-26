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

    private async Task<int> UploadDirectoryAsync(SshSession session, Settings settings)
    {
        var searchOption = settings.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var files = Directory.EnumerateFiles(settings.SourcePath, "*", searchOption).ToList();
        
        var uploadedFiles = 0;
        var failedFiles = new List<string>();

        var fileCountText = settings.Recursive ? "files" : $"{files.Count} files";
        AnsiConsole.MarkupLineInterpolated($"[blue]Uploading directory '{settings.SourcePath}' ({fileCountText})...[/]");

        foreach (var file in files)
        {
            var relativePath = Path.GetRelativePath(settings.SourcePath, file)
                .Replace(Path.DirectorySeparatorChar, '/');

            try
            {
                await using var fileStream = new FileStream(file, FileMode.Open, FileAccess.Read);
                var result = await session.TransferFileAsync(settings.TargetPath, relativePath, fileStream, settings.Overwrite, CancellationToken.None);
                
                if (result == 0)
                {
                    uploadedFiles++;
                    AnsiConsole.MarkupLineInterpolated($"[green]Uploaded: {Path.GetFileName(file)}[/]");
                }
                else if (result == ErrorCodes.FileExists)
                {
                    AnsiConsole.MarkupLineInterpolated($"[yellow]The file '{relativePath}' already exists at the destination and will not be overwritten.[/]");
                }
                else
                {
                    failedFiles.Add(file);
                    AnsiConsole.MarkupLineInterpolated($"[yellow]Failed to upload: {Path.GetFileName(file)}[/]");
                }
            }
            catch (Exception ex)
            {
                failedFiles.Add(file);
                AnsiConsole.MarkupLineInterpolated($"[yellow]Failed to upload {Path.GetFileName(file)}: {ex.Message}[/]");
            }
        }

        if (failedFiles.Count == 0)
        {
            AnsiConsole.MarkupLineInterpolated($"[green]Successfully uploaded {uploadedFiles} files to '{settings.TargetPath}'[/]");
            return 0;
        }

        AnsiConsole.MarkupLineInterpolated($"[yellow]Uploaded {uploadedFiles} files with {failedFiles.Count} failures:[/]");
        foreach (var failedFile in failedFiles)
        {
            AnsiConsole.MarkupLineInterpolated($"[dim]Failed: {Path.GetFileName(failedFile)}[/]");
        }
        return -1;
    }
}