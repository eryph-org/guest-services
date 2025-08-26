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
        await clientSession.ConnectAsync(clientStream);
        var isAuthenticated = await clientSession.AuthenticateAsync(new SshClientCredentials("egs", keyPair));
        if (!isAuthenticated)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Could not connect. The authentication failed.[/]");
            return -1;
        }

        // First try to download as a file
        var fileResult = await TryDownloadAsFileAsync(clientSession, settings);
        
        // If file download was successful, return
        if (fileResult == 0)
        {
            return fileResult;
        }
        
        // If file not found, try as directory
        if (fileResult == ErrorCodes.FileNotFound)
        {
            return await TryDownloadAsDirectoryAsync(clientSession, settings);
        }
        
        return fileResult;
    }

    private async Task<int> TryDownloadAsFileAsync(SshSession session, Settings settings)
    {
        // Check if target already exists and overwrite is not set
        if (File.Exists(settings.TargetPath) && !settings.Overwrite)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]The file '{settings.TargetPath}' already exists. Use --overwrite to replace it.[/]");
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
            await using var targetStream = new FileStream(settings.TargetPath, FileMode.Create, FileAccess.Write);
            var result = await session.DownloadFileAsync(settings.SourcePath, "", targetStream, CancellationToken.None);
            
            if (result == 0)
            {
                AnsiConsole.MarkupLineInterpolated($"[green]File downloaded successfully to '{settings.TargetPath}'.[/]");
            }
            else if (result != ErrorCodes.FileNotFound)
            {
                // Clean up the empty file we created for non-FileNotFound errors
                if (File.Exists(settings.TargetPath))
                {
                    File.Delete(settings.TargetPath);
                }
            }

            return result;
        }
        catch (DownloadFileServerException ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Download failed: {ex.Message}[/]");
            // Clean up the partial file
            if (File.Exists(settings.TargetPath))
            {
                File.Delete(settings.TargetPath);
            }
            return -1;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]An unexpected error occurred: {ex.Message}[/]");
            // Clean up the partial file
            if (File.Exists(settings.TargetPath))
            {
                File.Delete(settings.TargetPath);
            }
            return -1;
        }
    }

    private async Task<int> TryDownloadAsDirectoryAsync(SshSession session, Settings settings)
    {
        if (!settings.Recursive)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]The source '{settings.SourcePath}' appears to be a directory. Use --recursive to download directories.[/]");
            return -1;
        }

        try
        {
            var (listResult, files) = await session.ListDirectoryAsync(settings.SourcePath, CancellationToken.None);
            
            if (listResult == ErrorCodes.FileNotFound)
            {
                AnsiConsole.MarkupLineInterpolated($"[red]The path '{settings.SourcePath}' does not exist on the VM.[/]");
                return ErrorCodes.FileNotFound;
            }
            
            if (listResult != 0)
            {
                AnsiConsole.MarkupLineInterpolated($"[red]Failed to list directory '{settings.SourcePath}'. Error code: {listResult}[/]");
                return listResult;
            }

            return await DownloadDirectoryAsync(session, settings, files);
        }
        catch (DownloadFileServerException ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Directory listing failed: {ex.Message}[/]");
            return -1;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]An unexpected error occurred while listing directory: {ex.Message}[/]");
            return -1;
        }
    }

    private async Task<int> DownloadDirectoryAsync(SshSession session, Settings settings, List<RemoteFileInfo> files)
    {
        // Check if target directory exists and handle overwrite
        if (Directory.Exists(settings.TargetPath) && !settings.Overwrite)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]The target directory '{settings.TargetPath}' already exists. Use --overwrite to replace it.[/]");
            return ErrorCodes.FileExists;
        }

        // Create target directory
        if (!Directory.Exists(settings.TargetPath))
        {
            Directory.CreateDirectory(settings.TargetPath);
        }

        var downloadedFiles = 0;
        var totalFiles = files.Count(f => !f.IsDirectory);
        var failedFiles = new List<string>();

        AnsiConsole.MarkupLineInterpolated($"[blue]Downloading directory '{settings.SourcePath}' with {totalFiles} files...[/]");

        foreach (var file in files)
        {
            if (file.IsDirectory)
            {
                // Recursively download subdirectories
                var subDirSourcePath = file.FullPath.Replace('\\', '/');
                var subDirTargetPath = Path.Combine(settings.TargetPath, file.Name);
                
                var subDirSettings = new Settings
                {
                    VmId = settings.VmId,
                    SourcePath = subDirSourcePath,
                    TargetPath = subDirTargetPath,
                    Overwrite = settings.Overwrite,
                    Recursive = settings.Recursive
                };

                var subDirResult = await TryDownloadAsDirectoryAsync(session, subDirSettings);
                if (subDirResult != 0)
                {
                    failedFiles.Add(subDirSourcePath);
                }
            }
            else
            {
                // Download individual file
                var targetFilePath = Path.Combine(settings.TargetPath, file.Name);
                
                try
                {
                    // Create subdirectories if needed
                    var fileDirectory = Path.GetDirectoryName(targetFilePath);
                    if (!string.IsNullOrEmpty(fileDirectory) && !Directory.Exists(fileDirectory))
                    {
                        Directory.CreateDirectory(fileDirectory);
                    }

                    await using var targetStream = new FileStream(targetFilePath, FileMode.Create, FileAccess.Write);
                    var result = await session.DownloadFileAsync(file.FullPath.Replace('\\', '/'), "", targetStream, CancellationToken.None);
                    
                    if (result == 0)
                    {
                        downloadedFiles++;
                        AnsiConsole.MarkupLineInterpolated($"[dim]Downloaded: {file.Name}[/]");
                    }
                    else
                    {
                        failedFiles.Add(file.FullPath);
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
                    failedFiles.Add(file.FullPath);
                    AnsiConsole.MarkupLineInterpolated($"[yellow]Failed to download {file.Name}: {ex.Message}[/]");
                    
                    // Clean up failed file
                    var failedTargetFilePath = Path.Combine(settings.TargetPath, file.Name);
                    if (File.Exists(failedTargetFilePath))
                    {
                        File.Delete(failedTargetFilePath);
                    }
                }
            }
        }

        if (failedFiles.Count == 0)
        {
            AnsiConsole.MarkupLineInterpolated($"[green]Directory downloaded successfully! {downloadedFiles} files downloaded to '{settings.TargetPath}'.[/]");
            return 0;
        }
        else
        {
            AnsiConsole.MarkupLineInterpolated($"[yellow]Directory download completed with {failedFiles.Count} failures. {downloadedFiles} files downloaded successfully.[/]");
            foreach (var failedFile in failedFiles)
            {
                AnsiConsole.MarkupLineInterpolated($"[dim]Failed: {failedFile}[/]");
            }
            return -1;
        }
    }
}