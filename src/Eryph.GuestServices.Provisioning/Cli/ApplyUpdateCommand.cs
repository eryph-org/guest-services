using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Eryph.GuestServices.Provisioning.Cli;

/// <summary>
/// Internal updater verb — the out-of-process half of the self-update. Runs
/// from the <em>staged</em> copy (never from the install dir it replaces),
/// stops the service, swaps the install directory for the staged payload, and
/// restarts. On any failure after the swap it rolls back to the saved copy so a
/// bad build can't leave the guest without a working agent. This mirrors
/// eryph-zero's <c>SelfInstall</c> recipe.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ApplyUpdateCommand : AsyncCommand<ApplyUpdateCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--from <DIR>")]
        [Description("Staged payload directory (the verified new binaries).")]
        public string From { get; init; } = "";

        [CommandOption("--to <DIR>")]
        [Description("Install directory to replace (where the running agent lives).")]
        public string To { get; init; } = "";

        [CommandOption("--service <NAME>")]
        [Description("Windows service to stop/start around the swap.")]
        public string Service { get; init; } = "eryph-guest-services";
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.From) || !Directory.Exists(settings.From))
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Staged payload directory not found: {settings.From}[/]");
            return 2;
        }
        if (string.IsNullOrWhiteSpace(settings.To))
        {
            AnsiConsole.MarkupLine("[red]--to install directory is required.[/]");
            return 2;
        }

        var to = settings.To.TrimEnd(Path.DirectorySeparatorChar);
        var backup = to + ".old";
        var backupCreated = false;

        try
        {
            await StopServiceAsync(settings.Service).ConfigureAwait(false);

            if (Directory.Exists(backup))
                Directory.Delete(backup, recursive: true);

            // Move the live install dir aside in a retry loop: this both keeps a
            // rollback copy and waits out the file-lock release after the
            // service process exits (Directory.Move throws IOException while a
            // binary is still mapped).
            if (Directory.Exists(to))
            {
                await MoveWithRetryAsync(to, backup, TimeSpan.FromMinutes(1)).ConfigureAwait(false);
                backupCreated = true;
            }

            CopyDirectory(settings.From, to);

            await StartServiceAsync(settings.Service).ConfigureAwait(false);
            if (!await WaitForRunningAsync(settings.Service, TimeSpan.FromMinutes(2)).ConfigureAwait(false))
                throw new InvalidOperationException($"Service '{settings.Service}' did not reach Running after the update.");

            if (Directory.Exists(backup))
                Directory.Delete(backup, recursive: true);

            AnsiConsole.MarkupLine("[green]Update applied.[/]");
            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Update failed: {ex.Message}[/]");
            if (!backupCreated)
                return 1;

            AnsiConsole.MarkupLine("[yellow]Rolling back to the previous version...[/]");
            try
            {
                await StopServiceAsync(settings.Service).ConfigureAwait(false);
                if (Directory.Exists(to))
                    await MoveWithRetryAsync(to, to + ".failed", TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                try
                {
                    await MoveWithRetryAsync(backup, to, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                }
                catch
                {
                    // Restoring the old binary failed and `to` is now empty — put
                    // the (failed) new build back so the service still has
                    // SOMETHING to start, rather than leaving the guest with no
                    // binary at all, then resurface the failure.
                    if (!Directory.Exists(to) && Directory.Exists(to + ".failed"))
                        await MoveWithRetryAsync(to + ".failed", to, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                    throw;
                }
                await StartServiceAsync(settings.Service).ConfigureAwait(false);

                if (Directory.Exists(to + ".failed"))
                    Directory.Delete(to + ".failed", recursive: true);

                AnsiConsole.MarkupLine("[yellow]Rolled back to the previous version.[/]");
            }
            catch (Exception rollbackEx)
            {
                AnsiConsole.MarkupLineInterpolated($"[red]Rollback failed: {rollbackEx.Message}[/]");
            }

            return 1;
        }
    }

    private static async Task MoveWithRetryAsync(string source, string destination, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                Directory.Move(source, destination);
                return;
            }
            catch (IOException ex)
            {
                // A binary in `source` is still mapped (service not fully
                // exited yet). Drop any partial destination and wait.
                last = ex;
                if (Directory.Exists(destination))
                    Directory.Delete(destination, recursive: true);
                await Task.Delay(1000).ConfigureAwait(false);
            }
        }

        throw last ?? new TimeoutException($"Could not move '{source}' to '{destination}' within {timeout}.");
    }

    // Internal for unit tests. Rebuilds each path RELATIVE to `source` rather
    // than string-replacing `source` — a substring replace mangles paths where
    // the source name recurs, and silently no-ops when the OS returns a
    // differently-cased path than the caller passed.
    internal static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, dir)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)), overwrite: true);
    }

    private static async Task StopServiceAsync(string service)
    {
        // sc.exe stop returns 1062 (not started) / 1060 (not installed); both
        // are fine — we just need it not running before the swap.
        await RunScAsync(["stop", service]).ConfigureAwait(false);
        // Must actually reach STOPPED before we move the install dir — moving it
        // while a DLL is still mapped corrupts the running service.
        if (!await WaitForStateAsync(service, "STOPPED", TimeSpan.FromMinutes(1)).ConfigureAwait(false))
            throw new TimeoutException($"Service '{service}' did not reach STOPPED within the timeout.");
    }

    private static async Task StartServiceAsync(string service) =>
        await RunScAsync(["start", service]).ConfigureAwait(false);

    private static async Task<bool> WaitForRunningAsync(string service, TimeSpan timeout) =>
        await WaitForStateAsync(service, "RUNNING", timeout).ConfigureAwait(false);

    private static async Task<bool> WaitForStateAsync(string service, string state, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var (_, output) = await RunScAsync(["query", service]).ConfigureAwait(false);
            // sc query prints "STATE              : 4  RUNNING" / "1  STOPPED".
            if (output.Contains(state, StringComparison.OrdinalIgnoreCase))
                return true;
            // Service not installed -> nothing to wait for.
            if (output.Contains("1060", StringComparison.Ordinal)
                || output.Contains("does not exist", StringComparison.OrdinalIgnoreCase))
                return state.Equals("STOPPED", StringComparison.OrdinalIgnoreCase);
            await Task.Delay(1000).ConfigureAwait(false);
        }

        return false;
    }

    private static async Task<(int ExitCode, string Output)> RunScAsync(IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "sc.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start sc.exe.");
        var stdout = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
        var stderr = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
        await process.WaitForExitAsync().ConfigureAwait(false);
        return (process.ExitCode, stdout + stderr);
    }
}
