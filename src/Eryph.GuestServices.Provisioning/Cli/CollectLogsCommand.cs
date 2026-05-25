using System.ComponentModel;
using System.IO.Compression;
using System.Reflection;
using System.Text;
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

        // logs/
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
        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
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
        }
    }
}
