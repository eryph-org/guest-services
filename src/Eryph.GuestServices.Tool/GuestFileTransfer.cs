using Eryph.GuestServices.Core;
using Eryph.GuestServices.DevTunnels.Ssh.Extensions;
using Microsoft.DevTunnels.Ssh;
using Spectre.Console;

namespace Eryph.GuestServices.Tool;

// The transport-neutral file/directory transfer operations. The same pre-checks
// and console reporting run whether the session was established over the Hyper-V
// socket or the eryph channel; the actual byte transfer lives in the SshSession
// extension methods. Keeping this in one place is what lets the VM and catlet
// commands process the copy identically.
internal static class GuestFileTransfer
{
    public static async Task<int> UploadFileAsync(
        SshSession session, string sourcePath, string targetPath, bool overwrite)
    {
        if (!File.Exists(sourcePath))
        {
            AnsiConsole.MarkupLineInterpolated($"[red]The file '{sourcePath}' does not exist.[/]");
            return -1;
        }

        AnsiConsole.MarkupLineInterpolated($"[cyan]Uploading file '{sourcePath}'...[/]");

        await using var fileStream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read);
        var result = await session.UploadFileAsync(targetPath, fileStream, overwrite, CancellationToken.None);

        if (result == 0)
            AnsiConsole.MarkupLineInterpolated($"[green]File uploaded successfully to '{targetPath}'[/]");
        else if (result == ErrorCodes.FileExists)
            AnsiConsole.MarkupLineInterpolated($"[red]The file '{targetPath}' already exists.[/]");
        else
            AnsiConsole.MarkupLineInterpolated($"[red]Upload failed with error code: {result}[/]");

        return result;
    }

    public static async Task<int> UploadDirectoryAsync(
        SshSession session, string sourcePath, string targetPath, bool overwrite, bool recursive)
    {
        if (!Directory.Exists(sourcePath))
        {
            AnsiConsole.MarkupLineInterpolated($"[red]The directory '{sourcePath}' does not exist.[/]");
            return -1;
        }

        return await session.UploadDirectoryAsync(
            sourcePath, targetPath, overwrite, recursive,
            writeInfo: msg => AnsiConsole.MarkupLineInterpolated($"[cyan]{msg}[/]"),
            writeError: msg => AnsiConsole.MarkupLineInterpolated($"[red]{msg}[/]"),
            writeWarning: msg => AnsiConsole.MarkupLineInterpolated($"[orange3]{msg}[/]"),
            writeSuccess: msg => AnsiConsole.MarkupLineInterpolated($"[green]{msg}[/]"));
    }

    public static async Task<int> DownloadFileAsync(
        SshSession session, string sourcePath, string targetPath, bool overwrite)
    {
        if (File.Exists(targetPath) && !overwrite)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]The file '{targetPath}' already exists.[/]");
            return ErrorCodes.FileExists;
        }

        var targetDirectory = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(targetDirectory) && !Directory.Exists(targetDirectory))
            Directory.CreateDirectory(targetDirectory);

        int result;
        try
        {
            AnsiConsole.MarkupLineInterpolated($"[cyan]Downloading file '{sourcePath}'...[/]");

            await using (var targetStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write))
            {
                result = await session.DownloadFileAsync(sourcePath, targetStream, CancellationToken.None);
            }

            if (result == 0)
            {
                AnsiConsole.MarkupLineInterpolated($"[green]File downloaded successfully to '{targetPath}'[/]");
                return 0;
            }

            if (result == ErrorCodes.FileNotFound)
                AnsiConsole.MarkupLineInterpolated($"[red]The file '{sourcePath}' was not found on the guest.[/]");
            else
                AnsiConsole.MarkupLineInterpolated($"[red]Download failed with error code: {result}[/]");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Download failed: {ex.Message}[/]");
            result = -1;
        }

        // Remove the file this download attempt wrote when it did not complete, so
        // a failure never leaves a truncated/empty target behind. A file we refused
        // to overwrite is never deleted here: that case returns earlier, above. With
        // --overwrite the existing file was already replaced (FileMode.Create) before
        // the failure, so removing the partial result is the intended outcome.
        if (result != 0 && File.Exists(targetPath))
        {
            try
            {
                File.Delete(targetPath);
            }
            catch
            {
                // Best effort: leaving the partial file is no worse than failing here.
            }
        }

        return result;
    }

    public static async Task<int> DownloadDirectoryAsync(
        SshSession session, string sourcePath, string targetPath, bool overwrite, bool recursive)
    {
        // No local pre-check here (unlike the uploads): the source is on the guest,
        // so a missing-source error can only come back from the remote listing,
        // which the extension method reports through writeError.
        return await session.DownloadDirectoryAsync(
            sourcePath, targetPath, overwrite, recursive,
            writeInfo: msg => AnsiConsole.MarkupLineInterpolated($"[cyan]{msg}[/]"),
            writeError: msg => AnsiConsole.MarkupLineInterpolated($"[red]{msg}[/]"),
            writeWarning: msg => AnsiConsole.MarkupLineInterpolated($"[orange3]{msg}[/]"),
            writeSuccess: msg => AnsiConsole.MarkupLineInterpolated($"[green]{msg}[/]"));
    }
}
