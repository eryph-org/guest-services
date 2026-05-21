using System.Reflection;
using Eryph.GuestServices.HvDataExchange.Guest;
using Eryph.GuestServices.Provisioning.DataSources;
using Eryph.GuestServices.Provisioning.Hosting;
using Eryph.GuestServices.Provisioning.Reporting;
using Eryph.GuestServices.Provisioning.Serialization;
using Eryph.GuestServices.Provisioning.Stages;
using Eryph.GuestServices.Provisioning.State;
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
        // Data sources (probed in registration order by DataSourceLocator).
        container.Register<IVolumeProbe, DriveInfoVolumeProbe>(Lifestyle.Singleton);
        container.Collection.Append<IDataSource, NoCloudDataSource>(Lifestyle.Singleton);
        container.Collection.Append<IDataSource, ConfigDriveDataSource>(Lifestyle.Singleton);
        container.Register<IDataSourceLocator, DataSourceLocator>(Lifestyle.Singleton);

        // State.
        container.Register<IStateStore, FileStateStore>(Lifestyle.Singleton);

        // Stage runner.
        container.Register<IStageRunner, StageRunner>(Lifestyle.Singleton);

        // Handler discovery: scan this assembly for [Stage]-attributed IHandler types
        // so agent D can drop new handlers in without touching this file.
        var handlerTypes = HandlerDiscovery.DiscoverHandlers(Assembly.GetExecutingAssembly());
        container.Collection.Register(typeof(IHandler), handlerTypes, Lifestyle.Singleton);

        // Windows OS abstraction - the concrete WindowsOs is owned by agent D.
        container.Register<IWindowsOs, WindowsOs>(Lifestyle.Singleton);

        // Hyper-V KVP reporting.
        container.Register<IGuestDataExchange, WindowsGuestDataExchange>(Lifestyle.Singleton);
        container.Register<IHostStatusReporter, KvpHostStatusReporter>(Lifestyle.Singleton);

        // YAML serializer adapter.
        container.Register<ICloudConfigSerializer, CloudConfigSerializer>(Lifestyle.Singleton);
    }
}
