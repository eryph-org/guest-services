using System.ComponentModel;
using System.Runtime.Versioning;
using Eryph.GuestServices.CloudConfig;
using Eryph.GuestServices.Provisioning.Hosting;
using Eryph.GuestServices.Provisioning.Logging;
using Eryph.GuestServices.Provisioning.Update;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SimpleInjector;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Eryph.GuestServices.Provisioning.Cli;

/// <summary>
/// Diagnostic "what would self-update do" verb: runs the real
/// <see cref="IEgsUpdater.PrepareAsync"/> — resolve target, download, verify the
/// OpenPGP signature over SHA256SUMS, check the package hash, extract — and
/// reports the staged result WITHOUT applying it (no service stop / binary
/// swap). Lets the full download+verify path be exercised against the live
/// release index, and overrides target selection via <c>--version</c> /
/// <c>--channel</c>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class CheckUpdateCommand : AsyncCommand<CheckUpdateCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--version <VERSION>")]
        [Description("Force a specific target version (overrides --channel).")]
        public string? Version { get; init; }

        [CommandOption("--channel <CHANNEL>")]
        [Description("Release channel when no version is pinned: stable (default) or unstable.")]
        public string? Channel { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        using var container = ProvisioningContainerBuilder.Build();
        var hostBuilder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            ContentRootPath = AppContext.BaseDirectory,
        });
        hostBuilder.AddAgentLogging();
        hostBuilder.Services.AddSimpleInjector(container, opt => opt.AddLogging());
        using var host = hostBuilder.Build();
        host.Services.UseSimpleInjector(container);
        container.Verify();

        await host.StartAsync().ConfigureAwait(false);
        try
        {
            var updater = container.GetInstance<IEgsUpdater>();

            // Force opt-in; the verb's whole purpose is to check.
            var config = new EgsUpdateConfig
            {
                Enabled = true,
                Version = settings.Version,
                Channel = settings.Channel,
            };

            var plan = await updater.PrepareAsync(config, CancellationToken.None).ConfigureAwait(false);
            if (plan is null)
            {
                // No update due, or download/verify failed — EgsUpdater logged the
                // reason. Distinct marker so a test can tell this from a stage.
                AnsiConsole.MarkupLine("[yellow]NO-PLAN[/] (no update prepared; see log for the reason)");
                return 0;
            }

            // A returned plan means the signature AND signed hash verified — only
            // then does PrepareAsync stage the payload.
            AnsiConsole.MarkupLineInterpolated(
                $"STAGED {plan.TargetVersion} {plan.StagingDirectory}");
            return 0;
        }
        finally
        {
            await host.StopAsync().ConfigureAwait(false);
        }
    }
}
