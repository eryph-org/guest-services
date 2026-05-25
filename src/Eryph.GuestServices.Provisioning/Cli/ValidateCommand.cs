using System.ComponentModel;
using System.Runtime.Versioning;
using Eryph.GuestServices.CloudConfig;
using Eryph.GuestServices.CloudConfig.Validation;
using Eryph.GuestServices.Provisioning.Hosting;
using Eryph.GuestServices.Provisioning.UserData;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SimpleInjector;
using Spectre.Console;
using Spectre.Console.Cli;
using CloudConfigModel = Eryph.GuestServices.CloudConfig.CloudConfig;

namespace Eryph.GuestServices.Provisioning.Cli;

/// <summary>
/// Parses a local cloud-config file and runs the same validators the agent
/// uses at runtime, without applying anything. Useful in CI / authoring
/// workflows: exit code 0 means the file is loadable and semantically valid,
/// 1 means validation failed, 2 means it couldn't even be parsed.
/// <para>
/// <c>--target windows|linux|all</c> opts into a platform-portability check
/// driven by the source-generated <see cref="CloudConfigPlatformInventory"/>.
/// Non-supported fields surface as Warnings (the YAML still parses, so exit
/// code stays 0 — the warnings just flag non-portable usage to the author).
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ValidateCommand : AsyncCommand<ValidateCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--user-data <PATH>")]
        [Description("Path to the cloud-config user-data file.")]
        public string? UserDataPath { get; init; }

        [CommandOption("--target <TARGET>")]
        [Description("Platform portability check: 'windows', 'linux', or 'all' (default).")]
        public string? Target { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.UserDataPath))
        {
            AnsiConsole.MarkupLine("[red]--user-data <PATH> is required.[/]");
            return 2;
        }
        if (!File.Exists(settings.UserDataPath))
        {
            AnsiConsole.MarkupLineInterpolated($"[red]File not found: {settings.UserDataPath}[/]");
            return 2;
        }

        if (!TryParseTarget(settings.Target, out var target))
        {
            AnsiConsole.MarkupLineInterpolated(
                $"[red]Unknown --target value '{settings.Target}'. Expected 'windows', 'linux', or 'all'.[/]");
            return 2;
        }

        var bytes = await File.ReadAllBytesAsync(settings.UserDataPath).ConfigureAwait(false);

        using var container = ProvisioningContainerBuilder.Build();
        var hostBuilder = Host.CreateApplicationBuilder();
        hostBuilder.Services.AddLogging();
        hostBuilder.Services.AddSimpleInjector(container, opt => opt.AddLogging());
        using var host = hostBuilder.Build();
        host.Services.UseSimpleInjector(container);
        container.Verify();
        await host.StartAsync().ConfigureAwait(false);

        try
        {
            var pipeline = container.GetInstance<IUserDataPipeline>();

            ResolvedUserData resolved;
            try
            {
                resolved = await pipeline.ResolveAsync(bytes, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLineInterpolated($"[red]Failed to parse user-data: {ex.Message}[/]");
                return 2;
            }

            var validation = CloudConfigValidations.ValidateCloudConfig(resolved.CloudConfig);
            if (validation.IsFail)
            {
                AnsiConsole.MarkupLine("[red]Validation failed:[/]");
                foreach (var error in validation.FailToSeq())
                    AnsiConsole.MarkupLineInterpolated($"  [red]- {error.Message}[/]");
                return 1;
            }

            // Platform-portability surface — operator-visible Warnings for
            // fields present in the parsed config that don't list the
            // requested target platform. Driven entirely off the source-
            // generated inventory so the model is the source of truth.
            ReportPlatformPortability(resolved.CloudConfig, target);

            AnsiConsole.MarkupLine("[green]User-data is valid.[/]");
            return 0;
        }
        finally
        {
            await host.StopAsync().ConfigureAwait(false);
        }
    }

    private static bool TryParseTarget(string? value, out CloudInitPlatforms target)
    {
        if (string.IsNullOrWhiteSpace(value) || string.Equals(value, "all", StringComparison.OrdinalIgnoreCase))
        {
            target = CloudInitPlatforms.All;
            return true;
        }
        if (string.Equals(value, "windows", StringComparison.OrdinalIgnoreCase))
        {
            target = CloudInitPlatforms.Windows;
            return true;
        }
        if (string.Equals(value, "linux", StringComparison.OrdinalIgnoreCase))
        {
            target = CloudInitPlatforms.Linux;
            return true;
        }
        target = CloudInitPlatforms.None;
        return false;
    }

    private static void ReportPlatformPortability(CloudConfigModel config, CloudInitPlatforms target)
    {
        // --target all is the lenient default — no per-field portability
        // check. Existing acknowledged-key Info logging at deserialise time
        // already covers the "saw it, ignored it" surface.
        if (target == CloudInitPlatforms.All)
            return;

        foreach (var entry in CloudConfigPlatformInventory.Fields)
        {
            if (!entry.Present(config))
                continue;

            // A field is supported on the requested target iff its Platforms
            // flag set intersects with the target flag. When it doesn't, the
            // operator's YAML uses a key that has no behaviour on the target.
            if ((entry.Platforms & target) != 0)
            {
                AnsiConsole.MarkupLineInterpolated(
                    $"[grey]  - {entry.YamlName}: supported on {target.ToString().ToLowerInvariant()}[/]");
                continue;
            }

            AnsiConsole.MarkupLineInterpolated(
                $"[yellow]Warning: '{entry.YamlName}' is not supported on {target.ToString().ToLowerInvariant()} ({entry.Description}).[/]");
        }
    }
}
