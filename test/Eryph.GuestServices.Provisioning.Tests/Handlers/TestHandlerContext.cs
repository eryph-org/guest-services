using Eryph.GuestServices.Provisioning.DataSources;
using Eryph.GuestServices.Provisioning.Stages;
using Eryph.GuestServices.Provisioning.Windows;

namespace Eryph.GuestServices.Provisioning.Tests.Handlers;

internal sealed class TestHandlerContext(IWindowsOs os) : IHandlerContext
{
    public IWindowsOs Os { get; } = os;

    public DataSourceResult DataSource { get; } = new()
    {
        SourceName = "test",
        InstanceId = "test-instance",
    };
}
