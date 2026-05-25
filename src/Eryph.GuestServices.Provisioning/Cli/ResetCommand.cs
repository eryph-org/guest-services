using System.ComponentModel;
using Eryph.GuestServices.Provisioning.Configuration;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Eryph.GuestServices.Provisioning.Cli;

/// <summary>
/// Clears persistent provisioning state so the next agent run treats the
/// guest as a fresh instance. Mirrors <c>cloud-init clean</c>:
/// per-instance semaphores + state.json are always cleared; per-boot
/// semaphores are cleared unless <c>--keep-per-boot</c> is passed; per-once
/// semaphores survive unless <c>--reset-once</c> is passed.
/// <c>--logs</c> and <c>--scripts</c> also wipe the corresponding directories
/// under <c>%ProgramData%\eryph\provisioning</c>.
/// </summary>
public sealed class ResetCommand : AsyncCommand<ResetCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--logs")]
        [Description("Also delete the agent logs directory.")]
        public bool ClearLogs { get; init; }

        [CommandOption("--scripts")]
        [Description("Also delete the staged scripts directory.")]
        public bool ClearScripts { get; init; }

        [CommandOption("--reset-once")]
        [Description("Also clear per-once semaphores (otherwise they survive a reset, matching cloud-init).")]
        public bool ResetOnce { get; init; }

        [CommandOption("--keep-per-boot")]
        [Description("Keep per-boot semaphores. Per-boot is conceptually transient and is cleared by default.")]
        public bool KeepPerBoot { get; init; }

        [CommandOption("--state-dir <DIR>")]
        [Description("Override the state directory (default: %ProgramData%\\eryph\\provisioning).")]
        public string? StateDir { get; init; }

        // Kept as a no-op for backwards compat with scripts that may pass it.
        // Reset has no confirmation prompt (matches cloud-init's `clean`): state.json
        // is metadata only, OS mutations are already done, and modules are idempotent
        // so re-running provisioning after an accidental reset is harmless.
        [CommandOption("-y|--yes")]
        [Description("Compatibility no-op (reset never prompts).")]
        public bool Yes { get; init; }
    }

    public override Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.StateDir))
            ProvisioningPaths.RootOverride = settings.StateDir;

        // Build the deletion list. Items are tagged with a description so we
        // can give the operator a precise log line for each.
        var items = new List<(string Path, string Description)>
        {
            (ProvisioningPaths.StateFile, "state file"),
            (ProvisioningPaths.InstanceRoot, "per-instance semaphores + cache"),
            (ProvisioningPaths.LastSeenBootFile, "boot session marker"),
        };

        // Per-boot semaphores: cleared by default. The whole global sem
        // directory is selectively pruned (per-once kept unless --reset-once).
        if (!settings.KeepPerBoot)
            items.AddRange(EnumeratePerFrequencyFiles(ProvisioningPaths.GlobalSemaphoreDir, "per-boot",
                "per-boot semaphore"));

        if (settings.ResetOnce)
            items.AddRange(EnumeratePerFrequencyFiles(ProvisioningPaths.GlobalSemaphoreDir, "per-once",
                "per-once semaphore"));

        if (settings.ClearLogs) items.Add((ProvisioningPaths.LogsDirectory, "logs"));
        if (settings.ClearScripts)
            items.Add((ProvisioningPaths.ScriptsDirectory(ProvisioningSettings.LoadOrDefault()), "scripts"));

        AnsiConsole.MarkupLine("[yellow]Removing:[/]");
        foreach (var (p, d) in items)
            AnsiConsole.MarkupLineInterpolated($"  {p} ({d})");

        var removed = 0;
        foreach (var (path, _) in items)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                    removed++;
                    AnsiConsole.MarkupLineInterpolated($"[green]Deleted file:[/] {path}");
                }
                else if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                    removed++;
                    AnsiConsole.MarkupLineInterpolated($"[green]Deleted directory:[/] {path}");
                }
                else
                {
                    AnsiConsole.MarkupLineInterpolated($"[grey]Not present:[/] {path}");
                }
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLineInterpolated($"[red]Failed to delete {path}: {ex.Message}[/]");
                return Task.FromResult(1);
            }
        }

        AnsiConsole.MarkupLineInterpolated($"[green]Removed {removed} item(s).[/]");
        return Task.FromResult(0);
    }

    private static IEnumerable<(string Path, string Description)> EnumeratePerFrequencyFiles(
        string root, string frequencySuffix, string description)
    {
        if (!Directory.Exists(root))
            yield break;

        foreach (var file in Directory.EnumerateFiles(root, "*." + frequencySuffix))
            yield return (file, description);
    }
}
