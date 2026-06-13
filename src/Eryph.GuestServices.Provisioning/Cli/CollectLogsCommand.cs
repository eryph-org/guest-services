using System.ComponentModel;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using Eryph.GuestServices.Core;
using Eryph.GuestServices.Provisioning.Configuration;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Eryph.GuestServices.Provisioning.Cli;

/// <summary>
/// Bundles state.json, the logs directory, the staged scripts directory and
/// the agent's version info into a zip archive — the cloud-init
/// <c>collect-logs</c> equivalent. Missing inputs are skipped silently so
/// operators can run it at any point in the lifecycle.
/// </summary>
public sealed class CollectLogsCommand : AsyncCommand<CollectLogsCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<OUTPUT>")]
        [Description("Path of the zip archive to create.")]
        public string Output { get; init; } = "";

        [CommandOption("--state-dir <DIR>")]
        [Description("Override the state directory (default: %ProgramData%\\eryph\\provisioning).")]
        public string? StateDir { get; init; }
    }

    public override Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.StateDir))
            ProvisioningPaths.RootOverride = settings.StateDir;

        if (string.IsNullOrWhiteSpace(settings.Output))
        {
            AnsiConsole.MarkupLine("[red]Output path is required.[/]");
            return Task.FromResult(2);
        }

        var output = Path.GetFullPath(settings.Output);
        var outDir = Path.GetDirectoryName(output);
        if (!string.IsNullOrEmpty(outDir))
            Directory.CreateDirectory(outDir);

        if (File.Exists(output))
            File.Delete(output);

        using var archive = ZipFile.Open(output, ZipArchiveMode.Create);

        // state.json
        if (File.Exists(ProvisioningPaths.StateFile))
            archive.CreateEntryFromFile(ProvisioningPaths.StateFile, "state.json");

        // logs/ — the agent's own operational log (agent.log + rolled backups)
        // lives under the service-wide guest-services root; the provisioning
        // per-script logs live under the provisioning root. Both go under the
        // same logs/ prefix (filenames don't collide: agent.log vs <script>.log).
        if (Directory.Exists(AgentPaths.LogsDirectory))
            AddDirectory(archive, AgentPaths.LogsDirectory, "logs");

        if (Directory.Exists(ProvisioningPaths.LogsDirectory))
            AddDirectory(archive, ProvisioningPaths.LogsDirectory, "logs");

        // scripts/
        var scripts = ProvisioningPaths.ScriptsDirectory(ProvisioningSettings.LoadOrDefault());
        if (Directory.Exists(scripts))
            AddDirectory(archive, scripts, "scripts");

        // version.txt — useful when triaging a bundle
        var versionEntry = archive.CreateEntry("version.txt");
        using (var stream = versionEntry.Open())
        using (var writer = new StreamWriter(stream, Encoding.UTF8))
        {
            var assembly = Assembly.GetExecutingAssembly();
            writer.WriteLine($"Assembly: {assembly.GetName().Name}");
            writer.WriteLine($"Version:  {assembly.GetName().Version}");
            writer.WriteLine($"Informational: {assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion}");
            writer.WriteLine($"Collected: {DateTimeOffset.UtcNow:O}");
        }

        AnsiConsole.MarkupLineInterpolated($"[green]Wrote bundle to {output}[/]");
        return Task.FromResult(0);
    }

    private static void AddDirectory(ZipArchive archive, string sourceDir, string entryPrefix)
    {
        // IgnoreInaccessible so an unreadable subdirectory under a privileged
        // tree (e.g. /var/log on Linux) is skipped during the walk instead of
        // throwing UnauthorizedAccessException.
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
        };

        // Materialize the listing inside the try so a failure to enumerate the
        // directory itself — the root being unreadable, or a transient I/O error
        // mid-walk — skips the whole directory instead of aborting the command.
        // collect-logs is best-effort; this never throws.
        List<string> files;
        try
        {
            files = Directory.EnumerateFiles(sourceDir, "*", options).ToList();
        }
        catch (IOException)
        {
            return;
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        foreach (var file in files)
        {
            var rel = Path.GetRelativePath(sourceDir, file).Replace('\\', '/');
            var entryName = $"{entryPrefix}/{rel}";
            try
            {
                archive.CreateEntryFromFile(file, entryName);
            }
            catch (IOException)
            {
                // Skip files we can't open (locked log files, etc).
            }
            catch (UnauthorizedAccessException)
            {
                // Skip files we lack permission to read. The agent log dir can
                // live under a privileged location (e.g. /var/log on Linux), so
                // a non-elevated collect-logs must stay best-effort, not abort.
            }
        }
    }
}
