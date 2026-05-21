using Eryph.GuestServices.Provisioning.DataSources;
using Eryph.GuestServices.Provisioning.Modules;
using Eryph.GuestServices.Provisioning.Windows;

namespace Eryph.GuestServices.Provisioning.Tests.Modules;

internal sealed class TestModuleContext(IWindowsOs os) : IModuleContext
{
    public IWindowsOs Os { get; } = os;

    public DataSourceResult DataSource { get; } = new()
    {
        SourceName = "test",
        InstanceId = "test-instance",
    };
}
