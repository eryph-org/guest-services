using Eryph.GuestServices.Provisioning.DataSources;
using Eryph.GuestServices.Provisioning.Modules;
using Eryph.GuestServices.Provisioning.Windows;

namespace Eryph.GuestServices.Provisioning.Tests.Modules;

internal sealed class TestModuleContext : IModuleContext
{
    public TestModuleContext(IWindowsOs os, DataSourceResult? dataSource = null)
    {
        Os = os;
        DataSource = dataSource ?? new DataSourceResult
        {
            SourceName = "test",
            InstanceId = "test-instance",
        };
    }

    public IWindowsOs Os { get; }

    public DataSourceResult DataSource { get; }
}
