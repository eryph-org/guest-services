using System.Reflection;
using Eryph.GuestServices.HvDataExchange.Guest;
using Eryph.GuestServices.Provisioning.DataSources;
using Eryph.GuestServices.Provisioning.Hosting;
using Eryph.GuestServices.Provisioning.Modules;
using Eryph.GuestServices.Provisioning.Reporting;
using Eryph.GuestServices.Provisioning.Reporting.Handlers;
using Eryph.GuestServices.Provisioning.Serialization;
using Eryph.GuestServices.Provisioning.Stages;
using Eryph.GuestServices.Provisioning.State;
using Eryph.GuestServices.Provisioning.UserData;
using Eryph.GuestServices.Provisioning.UserData.Handlers;
using Eryph.GuestServices.Provisioning.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SimpleInjector;
using SimpleInjector.Lifestyles;

namespace Eryph.GuestServices.Provisioning;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
        });

        builder.Services.AddLogging();
        builder.Services.AddWindowsService(options =>
        {
            options.ServiceName = "eryph-provisioning";
        });

        var container = new Container();
        container.Options.DefaultScopedLifestyle = new AsyncScopedLifestyle();

        builder.Services.AddSimpleInjector(container, options =>
        {
            options.AddHostedService<ProvisioningWorker>();
            options.AddLogging();
        });

        ConfigureContainer(container);

        var host = builder.Build();
        host.Services.UseSimpleInjector(container);

        container.Verify();

        await host.RunAsync().ConfigureAwait(false);
        return 0;
    }

    internal static void ConfigureContainer(Container container)
    {
        // Data sources. The locator orders by IDataSource.Priority, not registration
        // order — Azure(10) -> EC2(20) -> NoCloud(30) -> ConfigDrive(40) -> Hyper-V KVP(50).
        // Azure is probed first so a Windows guest in Azure (where PA may still be
        // writing CustomData.bin) holds up the chain via WaitForReady instead of
        // falling through to a stale NoCloud/ConfigDrive volume.
        container.Register<IVolumeProbe, DriveInfoVolumeProbe>(Lifestyle.Singleton);
        container.Collection.Append<IDataSource, AzureDataSource>(Lifestyle.Singleton);
        container.Collection.Append<IDataSource, Ec2DataSource>(Lifestyle.Singleton);
        container.Collection.Append<IDataSource, NoCloudDataSource>(Lifestyle.Singleton);
        container.Collection.Append<IDataSource, ConfigDriveDataSource>(Lifestyle.Singleton);
        container.Collection.Append<IDataSource, HyperVKvpDataSource>(Lifestyle.Singleton);
        container.Register<IDataSourceLocator, DataSourceLocator>(Lifestyle.Singleton);

        // State.
        container.Register<IStateStore, FileStateStore>(Lifestyle.Singleton);

        // Stage runner.
        container.Register<IStageRunner, StageRunner>(Lifestyle.Singleton);

        // Module discovery: scan this assembly for [Stage]-attributed IModule types
        // so new modules can be dropped in without touching this file.
        var moduleTypes = ModuleDiscovery.DiscoverModules(Assembly.GetExecutingAssembly());
        container.Collection.Register(typeof(IModule), moduleTypes, Lifestyle.Singleton);

        // Windows OS abstraction.
        container.Register<IWindowsOs, WindowsOs>(Lifestyle.Singleton);

        // Reporting framework: dispatcher fans out to all registered handlers.
        container.Register<IGuestDataExchange, WindowsGuestDataExchange>(Lifestyle.Singleton);
        container.Register<IReportingDispatcher, ReportingDispatcher>(Lifestyle.Singleton);
        container.Collection.Append<IReportingHandler, LogReportingHandler>(Lifestyle.Singleton);
        container.Collection.Append<IReportingHandler, KvpReportingHandler>(Lifestyle.Singleton);

        // User-data pipeline. Sniffs the root content type, dispatches through
        // the handler chain, and recurses via multipart / #include URL handlers
        // until the user-data is fully settled.
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
