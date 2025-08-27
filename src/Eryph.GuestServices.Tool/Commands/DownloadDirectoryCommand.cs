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

            if (!isAuthenticated)
            {
                AnsiConsole.MarkupLineInterpolated($"[red]Failed to authenticate to the guest service.[/]");
                return -1;
            }

            return await DownloadDirectoryAsync(clientSession, settings.SourcePath, settings.TargetPath, settings.Overwrite, settings.Recursive);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Error: {ex.Message}[/]");
            return -1;
        }
    }

    private async Task<int> DownloadDirectoryAsync(SshSession session, string sourcePath, string targetPath, bool overwrite, bool recursive, CancellationToken cancellation = default)
    {
        List<RemoteFileInfo> files;
        try
        {
            var (listResult, filesList) = await session.ListDirectoryAsync(sourcePath, cancellation);
            if (listResult != 0)
            {
                AnsiConsole.MarkupLineInterpolated($"[red]Failed to list directory '{sourcePath}' - Error code: {listResult}[/]");
                return listResult;
            }
            
            files = filesList;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Error listing directory '{sourcePath}': {ex.Message}[/]");
            return -1;
        }

        // Ensure target directory exists (create if needed)
        if (!Directory.Exists(targetPath))
        {
            Directory.CreateDirectory(targetPath);
        }

        var downloadedFiles = 0;
        var failedFiles = new List<string>();

        // Count files in current directory
        var currentLevelFiles = files.Where(f => !f.IsDirectory).ToList();
        var fileCountText = recursive 
            ? $"directory (recursive - {currentLevelFiles.Count} files at root level)" 
            : $"directory ({currentLevelFiles.Count} files)";
        AnsiConsole.MarkupLineInterpolated($"[blue]Downloading {fileCountText} from '{sourcePath}'...[/]");

        foreach (var file in files)
        {
            if (file.IsDirectory && recursive)
            {
                // Recursively download subdirectories (only if --recursive flag is set)
                var subDirSourcePath = SshExtensionUtils.NormalizePath(file.FullPath);
                var subDirTargetPath = Path.Combine(targetPath, file.Name);
                
                var subDirResult = await DownloadDirectoryAsync(session, subDirSourcePath, subDirTargetPath, overwrite, recursive, cancellation);
                if (subDirResult != 0)
                {
                    failedFiles.Add(subDirSourcePath);
                }
            }
            else if (!file.IsDirectory)
            {
                // Download individual file (only if it's not a directory)
                var targetFilePath = Path.Combine(targetPath, file.Name);
                
                // Create target directory for the file if it doesn't exist
                var fileDirectory = Path.GetDirectoryName(targetFilePath);
                if (!string.IsNullOrEmpty(fileDirectory) && !Directory.Exists(fileDirectory))
                {
                    Directory.CreateDirectory(fileDirectory);
                }

                try
                {
                    // Check if file exists and handle overwrite
                    if (File.Exists(targetFilePath) && !overwrite)
                    {
                        AnsiConsole.MarkupLineInterpolated($"[yellow]Skipped: {file.Name} (already exists)[/]");
                        continue;
                    }

                    await using var targetStream = new FileStream(targetFilePath, FileMode.Create, FileAccess.Write);
                    var result = await session.DownloadFileAsync(SshExtensionUtils.NormalizePath(file.FullPath), "", targetStream, cancellation);
                    
                    if (result == 0)
                    {
                        downloadedFiles++;
                    }
                    else
                    {
                        failedFiles.Add(SshExtensionUtils.NormalizePath(file.FullPath));
                        AnsiConsole.MarkupLineInterpolated($"[yellow]Failed to download: {file.Name}[/]");
                        
                        // Clean up failed file
                        if (File.Exists(targetFilePath))
                        {
                            File.Delete(targetFilePath);
                        }
                    }
                }
                catch (Exception ex)
                {
                    failedFiles.Add(SshExtensionUtils.NormalizePath(file.FullPath));
                    AnsiConsole.MarkupLineInterpolated($"[yellow]Failed to download {file.Name}: {ex.Message}[/]");
                    
                    // Clean up failed file
                    var failedTargetFilePath = Path.Combine(targetPath, file.Name);
                    if (File.Exists(failedTargetFilePath))
                    {
                        try
                        {
                            File.Delete(failedTargetFilePath);
                        }
                        catch
                        {
                            // Ignore cleanup failures
                        }
                    }
                }
            }
        }

        if (failedFiles.Count == 0)
        {
            AnsiConsole.MarkupLineInterpolated($"[green]Successfully downloaded {downloadedFiles} files to '{targetPath}'[/]");
            return 0;
        }

        AnsiConsole.MarkupLineInterpolated($"[yellow]Downloaded {downloadedFiles} files with {failedFiles.Count} failures:[/]");
        foreach (var failedFile in failedFiles)
        {
            AnsiConsole.MarkupLineInterpolated($"[dim]Failed: {failedFile}[/]");
        }
        return -1;
    }
}