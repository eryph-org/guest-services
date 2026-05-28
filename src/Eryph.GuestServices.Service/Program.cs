using System.Diagnostics;
using Eryph.GuestServices.Core;
using Eryph.GuestServices.DevTunnels.Ssh.Extensions;
using Eryph.GuestServices.HvDataExchange.Guest;
using Eryph.GuestServices.Provisioning.Cli;
using Eryph.GuestServices.Provisioning.Hosting;
using Eryph.GuestServices.Service.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SimpleInjector;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Eryph.GuestServices.Service;

internal static class Program
{
    // Recognised CLI subcommands. When the first argument matches one of these,
    // egs-service.exe behaves as the operator CLI; otherwise it falls back to
    // the Windows-service host (the SCM invokes the binary with no args).
    private static readonly HashSet<string> KnownSubcommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "run", "status", "reset", "collect-logs", "validate", "version",
        // Spectre's built-in help / version flags also short-circuit before
        // dispatching to a command.
        "--help", "-h", "--version",
    };

    public static async Task<int> Main(string[] args)
    {
        if (IsCliInvocation(args))
            return await RunCliAsync(args).ConfigureAwait(false);

        await RunServiceAsync(args).ConfigureAwait(false);
        return Environment.ExitCode;
    }

    private static bool IsCliInvocation(string[] args)
    {
        if (args.Length == 0)
            return false;

        return KnownSubcommands.Contains(args[0]);
    }

    // ---- CLI dispatch (operator workflows) ----

    private static async Task<int> RunCliAsync(string[] args)
    {
        var app = new CommandApp();
        app.Configure(config =>
        {
            config.SetApplicationName("egs-service");
            config.SetExceptionHandler((ex, _) =>
            {
                AnsiConsole.WriteException(ex, ExceptionFormats.ShortenEverything);
                return ex.HResult == 0 ? 1 : ex.HResult;
            });

            config.AddCommand<StatusCommand>("status")
                .WithDescription("Print the current provisioning state.");

            config.AddCommand<ResetCommand>("reset")
                .WithDescription("Delete state.json (and optionally logs / scripts) for fresh re-provisioning.");

            config.AddCommand<CollectLogsCommand>("collect-logs")
                .WithDescription("Bundle state, logs and scripts into a zip archive.");

            config.AddCommand<VersionCommand>("version")
                .WithDescription("Print the agent version.");

            // run and validate use Windows-only APIs (WindowsOs, KVP, etc.).
            // Only register them when running on Windows; on Linux the service
            // starts without provisioning support.
            if (OperatingSystem.IsWindows())
                AddWindowsProvisioningCommands(config);
        });

        return await app.RunAsync(args).ConfigureAwait(false);
    }

    // ---- Windows-service hosted run (default; matches SCM invocation) ----

    private static async Task RunServiceAsync(string[] args)
    {
        Trace.Listeners.Add(new ConsoleTraceListener());

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
        });

        builder.Services.AddLogging();
        builder.Services.AddSingleton<IServiceControlFlags, PlatformServiceControlFlags>();
        builder.Services.AddHostedService<SshServerService>();
        builder.Services.AddSingleton<IHostKeyGenerator, HostKeyGenerator>();
        builder.Services.AddSingleton<IClientKeyProvider, ClientKeyProvider>();
        builder.Services.AddSingleton<IShellSelector, KvpShellSelector>();

        if (OperatingSystem.IsWindows())
        {
            builder.Services.AddSingleton<IKeyStorage, WindowsKeyStorage>();
            builder.Services.AddSingleton<IGuestDataExchange, WindowsGuestDataExchange>();
            builder.Services.AddWindowsService(options =>
            {
                options.ServiceName = "eryph guest services";
            });
        }
        else if (OperatingSystem.IsLinux())
        {
            builder.Services.AddSingleton<IKeyStorage, LinuxKeyStorage>();
            builder.Services.AddSingleton<IGuestDataExchange, LinuxGuestDataExchange>();
            builder.Services.AddSystemd();
        }

        // Provisioning is currently Windows-only (it talks to KVP / WindowsOs).
        // Skip the registration on non-Windows hosts; the rest of the service
        // (SSH, key storage, etc.) is portable and runs everywhere.
        Container? provisioningContainer = null;
        if (OperatingSystem.IsWindows())
        {
            provisioningContainer = new Container();
            provisioningContainer.Options.ResolveUnregisteredConcreteTypes = true;
            RegisterProvisioningInto(provisioningContainer);

            builder.Services.AddSimpleInjector(provisioningContainer, options =>
            {
                options.AddHostedService<ProvisioningHostedService>();
                options.AddLogging();
            });
        }

        var host = builder.Build();

        if (provisioningContainer is not null)
        {
            host.Services.UseSimpleInjector(provisioningContainer);
            provisioningContainer.Verify();
        }

        host.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("Eryph.GuestServices.Service.Program")
            .LogInformation(
                "Starting eryph guest services {Version}...",
                GitVersionInformation.InformationalVersion);

        try
        {
            await host.RunAsync().ConfigureAwait(false);
        }
        finally
        {
            if (host is IAsyncDisposable asyncDisposable)
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            else
                host.Dispose();

            provisioningContainer?.Dispose();
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void RegisterProvisioningInto(Container container) =>
        ProvisioningContainerBuilder.RegisterInto(container);

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void AddWindowsProvisioningCommands(IConfigurator config)
    {
        config.AddCommand<RunCommand>("run")
            .WithDescription("Run the provisioning agent once (synchronous one-shot).");
        config.AddCommand<ValidateCommand>("validate")
            .WithDescription("Validate a cloud-config user-data file without applying it.");
    }
}
