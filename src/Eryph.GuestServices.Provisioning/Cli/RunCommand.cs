using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using Eryph.GuestServices.Provisioning.DataSources;
using Eryph.GuestServices.Provisioning.Hosting;
using Eryph.GuestServices.Provisioning.Logging;
using Eryph.GuestServices.Provisioning.Reporting;
using Eryph.GuestServices.Provisioning.Reporting.Events;
using Eryph.GuestServices.Provisioning.Stages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SimpleInjector;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Eryph.GuestServices.Provisioning.Cli;

/// <summary>
/// One-shot operator subcommand: runs the configured stages synchronously and
/// exits. The Windows-service hosted run lives in <c>egs-service</c> (which
/// embeds the provisioning library); this command always executes in-process
/// and returns. Supports <c>--dry-run</c>, <c>--stage</c>, <c>--user-data</c>
/// and <c>--instance-id</c> for operator workflows.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RunCommand : AsyncCommand<RunCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--dry-run")]
        [Description("What-if mode: log intended actions without mutating the guest.")]
        public bool DryRun { get; init; }

        [CommandOption("--stage <STAGE>")]
        [Description("Run a single stage only: local, network, config, or final.")]
        public string? Stage { get; init; }

        [CommandOption("--user-data <PATH>")]
        [Description("Override cloud-config user-data with the contents of a local file.")]
        public string? UserDataPath { get; init; }

        [CommandOption("--instance-id <ID>")]
        [Description("Override the instance id (forces fresh-instance treatment).")]
        public string? InstanceId { get; init; }

        [CommandOption("--state-dir <DIR>")]
        [Description("Override the state directory (default: %ProgramData%\\eryph\\provisioning).")]
        public string? StateDir { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.StateDir))
            ProvisioningPaths.RootOverride = settings.StateDir;

        // Build the override datasource if either of the override options is set.
        IDataSource? overrideSource = null;
        if (settings.UserDataPath is not null || settings.InstanceId is not null)
        {
            byte[]? userData = null;
            if (settings.UserDataPath is not null)
            {
                if (!File.Exists(settings.UserDataPath))
                {
                    AnsiConsole.MarkupLineInterpolated(
                        $"[red]User-data file not found: {settings.UserDataPath}[/]");
                    return 2;
                }
                // Read as bytes — user-data files are often gzipped multipart MIME
                // whose bytes are not valid UTF-8. See DataSourceResult.UserData.
                userData = await File.ReadAllBytesAsync(settings.UserDataPath).ConfigureAwait(false);
            }

            var instanceId = settings.InstanceId ?? "cli-override-" + Guid.NewGuid().ToString("N")[..8];
            overrideSource = new OverrideDataSource(instanceId, userData);
        }

        Stage? singleStage = null;
        if (!string.IsNullOrWhiteSpace(settings.Stage))
        {
            if (!TryParseStage(settings.Stage, out var parsed))
            {
                AnsiConsole.MarkupLineInterpolated(
                    $"[red]Unknown stage '{settings.Stage}'. Valid: local, network, config, final.[/]");
                return 2;
            }
            singleStage = parsed;
        }

        return await RunOnceAsync(settings, overrideSource, singleStage).ConfigureAwait(false);
    }

    private static async Task<int> RunOnceAsync(
        Settings settings,
        IDataSource? overrideSource,
        Stage? singleStage)
    {
        using var container = ProvisioningContainerBuilder.Build(new ProvisioningContainerOptions
        {
            DryRun = settings.DryRun,
            OverrideDataSource = overrideSource,
        });

        // SimpleInjector needs a host-side ILogger pipeline to satisfy
        // ILogger<T> injection. Build a minimal Generic Host shell that only
        // wires logging, hand the container over, and resolve from it.
        // ContentRootPath = the exe dir so appsettings.json (the Serilog
        // housekeeping config) is found from the one-shot `run` path too.
        var hostBuilder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            ContentRootPath = AppContext.BaseDirectory,
        });
        // Mirror the run into the agent log so `collect-logs` captures it too —
        // the one-shot `egs-service run` path otherwise only writes to console.
        hostBuilder.AddAgentFileLogging();
        hostBuilder.Services.AddSimpleInjector(container, opt => opt.AddLogging());
        using var host = hostBuilder.Build();
        host.Services.UseSimpleInjector(container);
        container.Verify();

        await host.StartAsync().ConfigureAwait(false);
        try
        {
            var runner = container.GetInstance<IStageRunner>();
            var reporter = container.GetInstance<IReportingDispatcher>();
            var logger = container.GetInstance<ILogger<RunCommand>>();

            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, ev) =>
            {
                ev.Cancel = true;
                cts.Cancel();
            };

            if (settings.DryRun)
                AnsiConsole.MarkupLine("[yellow]Running in DRY-RUN mode — no changes will be applied.[/]");

            StageRunOutcome outcome;
            try
            {
                outcome = singleStage is { } s
                    ? await runner.RunStageAsync(s, cts.Token).ConfigureAwait(false)
                    : await runner.RunAsync(cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled exception in stage runner");
                await reporter.EmitAsync(
                    new ReportingEvent.ProvisioningFailed(ex.Message, ex) { Origin = "run-command" },
                    CancellationToken.None).ConfigureAwait(false);
                AnsiConsole.WriteException(ex, ExceptionFormats.ShortenEverything);
                return 1;
            }

            return ReportOutcome(outcome, dryRun: settings.DryRun);
        }
        finally
        {
            await host.StopAsync().ConfigureAwait(false);
        }
    }

    private static int ReportOutcome(StageRunOutcome outcome, bool dryRun)
    {
        switch (outcome)
        {
            case StageRunOutcome.Success:
                AnsiConsole.MarkupLine("[green]Provisioning completed.[/]");
                return 0;

            case StageRunOutcome.NoDataSource:
                AnsiConsole.MarkupLine("[yellow]No data source available; nothing to provision.[/]");
                return 0;

            case StageRunOutcome.RebootRequested reboot:
                AnsiConsole.MarkupLineInterpolated(
                    $"[yellow]Reboot requested: {reboot.Reason}[/]");
                if (dryRun)
                {
                    AnsiConsole.MarkupLine("[grey]DRY-RUN: reboot not triggered.[/]");
                    return 0;
                }
                TriggerReboot();
                return 0;

            case StageRunOutcome.Failed failed:
                AnsiConsole.MarkupLineInterpolated($"[red]Provisioning failed: {failed.Reason}[/]");
                return 1;
        }

        return 1;
    }

    private static void TriggerReboot()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "shutdown.exe",
                Arguments = "/r /t 5 /c \"eryph provisioning reboot\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch
        {
            // Best-effort.
        }
    }

    internal static bool TryParseStage(string raw, out Stage stage)
    {
        switch (raw.Trim().ToLowerInvariant())
        {
            case "local":
                stage = Stages.Stage.Local;
                return true;
            case "network":
                stage = Stages.Stage.Network;
                return true;
            case "config":
                stage = Stages.Stage.Config;
                return true;
            case "final":
                stage = Stages.Stage.Final;
                return true;
            default:
                stage = default;
                return false;
        }
    }
}
