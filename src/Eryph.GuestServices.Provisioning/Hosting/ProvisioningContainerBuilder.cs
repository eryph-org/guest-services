using System.Runtime.Versioning;
using Eryph.GuestServices.Core;
using Eryph.GuestServices.HvDataExchange.Guest;
using Eryph.GuestServices.Provisioning.Configuration;
using Eryph.GuestServices.Provisioning.DataSources;
using Eryph.GuestServices.Provisioning.Modules;
using Eryph.GuestServices.Provisioning.Reporting;
using Eryph.GuestServices.Provisioning.Reporting.Handlers;
using Eryph.GuestServices.Provisioning.Semaphores;
using Eryph.GuestServices.Provisioning.Serialization;
using Eryph.GuestServices.Provisioning.Stages;
using Eryph.GuestServices.Provisioning.State;
using Eryph.GuestServices.Provisioning.UserData;
using Eryph.GuestServices.Provisioning.UserData.Handlers;
using Eryph.GuestServices.Provisioning.Windows;
using Microsoft.Extensions.Logging;
using SimpleInjector;
using SimpleInjector.Lifestyles;

namespace Eryph.GuestServices.Provisioning.Hosting;

/// <summary>
/// Composition root used by every CLI subcommand. The legacy <c>Program.Main</c>
/// hosted everything in one place; with subcommands we instead build the
/// container per command so dry-run can swap out the OS implementation without
/// touching the rest of the graph.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ProvisioningContainerBuilder
{
    public static Container Build(ProvisioningContainerOptions? options = null)
    {
        var container = new Container();
        // Spectre.Console.Cli resolves the command instance itself (a concrete
        // class) via ITypeResolver. Allow SimpleInjector to construct those
        // concrete classes on demand so we don't have to enumerate every
        // command type here. All of the command class's *dependencies* are
        // registered above, which is what matters for verification.
        container.Options.ResolveUnregisteredConcreteTypes = true;

        RegisterInto(container, options);

        return container;
    }

    /// <summary>
    /// Registers the provisioning library's services into an existing
    /// SimpleInjector container. Used by <c>egs-service</c> to graft
    /// provisioning into its own composition root without building a separate
    /// container per command.
    /// </summary>
    public static void RegisterInto(Container container, ProvisioningContainerOptions? options = null)
    {
        options ??= new ProvisioningContainerOptions();

        // SimpleInjector's AddSimpleInjector + AddHostedService integration
        // resolves hosted services from a scope, so the container needs a
        // default scoped lifestyle. Without this, the host crashes with
        // InvalidOperationException at startup ("The DefaultScopedLifestyle
        // property can not be changed after a lock..." etc.). The check makes
        // RegisterInto idempotent — callers that already configured a scoped
        // lifestyle keep theirs.
        if (container.Options.DefaultScopedLifestyle is null)
            container.Options.DefaultScopedLifestyle = new AsyncScopedLifestyle();

        // Settings: caller-provided (tests), else loaded from disk if present,
        // otherwise defaults. Modules and helpers depend on ProvisioningSettings
        // directly; tunables that were previously public constants live here so
        // they're operator-configurable.
        container.RegisterInstance(options.Settings ?? ProvisioningSettings.LoadOrDefault());

        // Operator on/off flags (HKLM\SOFTWARE\eryph\guest-services). Injected so
        // ProvisioningHostedService can gate the first-boot run. Opt-out: ON
        // unless an explicit REG_DWORD 0 turns it off.
        container.Register<IServiceControlFlags, RegistryServiceControlFlags>(Lifestyle.Singleton);

        // Data sources. The locator orders by IDataSource.Priority, not registration
        // order — Azure(10) -> EC2(20) -> NoCloud(30) -> ConfigDrive(40) ->
        // OpenStack metadata service(50).
        container.Register<IVolumeProbe, DriveInfoVolumeProbe>(Lifestyle.Singleton);

        // Azure detection probe (registry VmId + chassis asset tag). Injected so
        // datasources never read the ambient host platform statically — keeps the
        // gate deterministic under test even on an Azure-hosted build agent.
        container.Register<IPlatformProbe, PlatformProbe>(Lifestyle.Singleton);

        // An override datasource (e.g. --user-data / --instance-id from the CLI)
        // is prepended ahead of everything else so it always wins discovery.
        if (options.OverrideDataSource is not null)
        {
            var overrideInstance = options.OverrideDataSource;
            var registration = Lifestyle.Singleton.CreateRegistration(
                typeof(IDataSource),
                () => overrideInstance,
                container);
            container.Collection.Append(typeof(IDataSource), registration);
        }

        container.Collection.Append<IDataSource, AzureDataSource>(Lifestyle.Singleton);
        container.Collection.Append<IDataSource, Ec2DataSource>(Lifestyle.Singleton);
        // NoCloud consumes IUrlHelper to follow a meta-data `seedfrom:` pointer
        // (registered below in the user-data pipeline section). SimpleInjector
        // resolves the dependency regardless of registration order.
        container.Collection.Append<IDataSource, NoCloudDataSource>(Lifestyle.Singleton);
        container.Collection.Append<IDataSource, ConfigDriveDataSource>(Lifestyle.Singleton);
        // OpenStack metadata service (HTTP) — same openstack/<v>/ format as
        // ConfigDrive, fetched over the link-local endpoint instead of a disk.
        container.Collection.Append<IDataSource, OpenStackMetadataDataSource>(Lifestyle.Singleton);
        container.Register<IDataSourceLocator, DataSourceLocator>(Lifestyle.Singleton);

        // State. FileStateStore exposes a second public constructor for tests
        // (taking an explicit directory) which confuses SimpleInjector — register
        // via a factory to select the DI constructor unambiguously.
        if (options.DryRun)
        {
            container.Register<IStateStore, NullStateStore>(Lifestyle.Singleton);
            container.Register<ISemaphoreStore, NullSemaphoreStore>(Lifestyle.Singleton);
            container.Register<IScriptCheckpointStore, NullScriptCheckpointStore>(Lifestyle.Singleton);
            container.Register<IRuncmdCheckpointStore, NullRuncmdCheckpointStore>(Lifestyle.Singleton);
            container.Register<IDataSourceCache, NullDataSourceCache>(Lifestyle.Singleton);
        }
        else
        {
            container.Register<IStateStore>(
                () => new FileStateStore(container.GetInstance<ILogger<FileStateStore>>()),
                Lifestyle.Singleton);
            // FileDataSourceCache has a test-only secondary constructor; pin the DI
            // constructor via a factory (same pattern as FileStateStore).
            container.Register<IDataSourceCache>(
                () => new FileDataSourceCache(container.GetInstance<ILogger<FileDataSourceCache>>()),
                Lifestyle.Singleton);
            // Same FileSemaphoreStore secondary constructor issue — pin the DI
            // constructor explicitly via a factory.
            container.Register<ISemaphoreStore>(
                () => new FileSemaphoreStore(container.GetInstance<ILogger<FileSemaphoreStore>>()),
                Lifestyle.Singleton);
            // FileScriptCheckpointStore exposes a test-only secondary constructor;
            // pin the DI constructor via a factory.
            container.Register<IScriptCheckpointStore>(
                () => new FileScriptCheckpointStore(container.GetInstance<ILogger<FileScriptCheckpointStore>>()),
                Lifestyle.Singleton);
            // FileRuncmdCheckpointStore: same test-only secondary constructor pattern.
            container.Register<IRuncmdCheckpointStore>(
                () => new FileRuncmdCheckpointStore(container.GetInstance<ILogger<FileRuncmdCheckpointStore>>()),
                Lifestyle.Singleton);
        }

        // Boot session detection. Win32BootClock reads Win32_OperatingSystem.LastBootUpTime
        // via CIM. In dry-run mode we still register it so the container verifies
        // cleanly — the detector itself is harmless to invoke.
        container.Register<IBootClock, Win32BootClock>(Lifestyle.Singleton);
        // BootSessionDetector exposes a second public constructor for tests
        // (with explicit marker path) which SimpleInjector cannot disambiguate;
        // use a factory to pin the DI constructor.
        container.Register<IBootSessionDetector>(
            () => new BootSessionDetector(
                container.GetInstance<IBootClock>(),
                container.GetInstance<ILogger<BootSessionDetector>>()),
            Lifestyle.Singleton);

        // Stage runner.
        container.Register<IStageRunner, StageRunner>(Lifestyle.Singleton);

        // Module registration — explicit list; trim-safe (no Assembly.GetTypes()).
        container.Collection.Register(typeof(IModule), ModuleRegistry.ModuleTypes, Lifestyle.Singleton);

        // Default-user resolution (RFC 0018). Resolves which account top-level
        // credential shorthands target; consumed by the ssh / password modules.
        container.Register<IDefaultUserResolver, DefaultUserResolver>(Lifestyle.Singleton);

        // Windows OS abstraction. In dry-run mode we wrap the real WindowsOs
        // in a DryRunWindowsOs decorator so reads pass through to the real
        // guest, while writes are intercepted and logged. SimpleInjector's
        // RegisterDecorator handles the IWindowsOs -> DryRunWindowsOs(IWindowsOs)
        // composition cleanly.
        container.Register<IWindowsOs, WindowsOs>(Lifestyle.Singleton);
        if (options.DryRun)
            container.RegisterDecorator<IWindowsOs, DryRunWindowsOs>(Lifestyle.Singleton);

        // Reporting framework.
        container.Register<IGuestDataExchange, WindowsGuestDataExchange>(Lifestyle.Singleton);
        container.Register<IReportingDispatcher, ReportingDispatcher>(Lifestyle.Singleton);
        container.Collection.Append<IReportingHandler, LogReportingHandler>(Lifestyle.Singleton);

        // KVP reporting is disabled in dry-run mode so a what-if run does not
        // overwrite the host-visible KVP state of a real provisioning attempt.
        if (!options.DryRun)
            container.Collection.Append<IReportingHandler, KvpReportingHandler>(Lifestyle.Singleton);

        // User-data pipeline.
        container.Register<IUrlHelper, UrlHelper>(Lifestyle.Singleton);
        container.Collection.Append<IUserDataHandler, MultipartMimeHandler>(Lifestyle.Singleton);
        container.Collection.Append<IUserDataHandler, IncludeUrlHandler>(Lifestyle.Singleton);
        container.Collection.Append<IUserDataHandler, CloudConfigPartHandler>(Lifestyle.Singleton);
        container.Collection.Append<IUserDataHandler, ShellScriptPartHandler>(Lifestyle.Singleton);
        container.Collection.Append<IUserDataHandler, BoothookPartHandler>(Lifestyle.Singleton);
        container.Register<IUserDataPipeline, UserDataPipeline>(Lifestyle.Singleton);

        // YAML serializer adapter.
        container.Register<ICloudConfigSerializer, CloudConfigSerializer>(Lifestyle.Singleton);
    }
}

internal sealed class ProvisioningContainerOptions
{
    /// <summary>
    /// When true the OS write methods are intercepted (see DryRunWindowsOs),
    /// state.json is not persisted, and KVP reporting is disabled. Reads still
    /// flow through to the real guest so handlers observe accurate state.
    /// </summary>
    public bool DryRun { get; init; }

    /// <summary>
    /// Optional priority-zero datasource used to inject user-data / instance-id
    /// from the CLI. Prepended ahead of the discovery chain.
    /// </summary>
    public IDataSource? OverrideDataSource { get; init; }

    /// <summary>
    /// Optional pre-resolved settings. When null the container loads them from
    /// disk (<see cref="ProvisioningSettings.LoadOrDefault"/>). Lets callers
    /// (and tests) supply settings without depending on the on-disk
    /// egs-provisioning.json next to the binary.
    /// </summary>
    public ProvisioningSettings? Settings { get; init; }
}
