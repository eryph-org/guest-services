using System.ComponentModel;
using Eryph.GuestServices.CloudConfig.Validation;
using Eryph.GuestServices.Provisioning.Hosting;
using Eryph.GuestServices.Provisioning.UserData;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SimpleInjector;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Eryph.GuestServices.Provisioning.Cli;

/// <summary>
/// Parses a local cloud-config file and runs the same validators the agent
/// uses at runtime, without applying anything. Useful in CI / authoring
/// workflows: exit code 0 means the file is loadable and semantically valid,
/// 1 means validation failed, 2 means it couldn't even be parsed.
/// </summary>
public sealed class ValidateCommand : AsyncCommand<ValidateCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--user-data <PATH>")]
        [Description("Path to the cloud-config user-data file.")]
        public string? UserDataPath { get; init; }
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

            AnsiConsole.MarkupLine("[green]User-data is valid.[/]");
            return 0;
        }
        finally
        {
            await host.StopAsync().ConfigureAwait(false);
        }
    }
}
