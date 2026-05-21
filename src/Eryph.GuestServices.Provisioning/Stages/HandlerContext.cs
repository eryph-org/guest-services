using Eryph.GuestServices.Provisioning.DataSources;
using Eryph.GuestServices.Provisioning.Windows;

namespace Eryph.GuestServices.Provisioning.Stages;

internal sealed class HandlerContext(IWindowsOs os, DataSourceResult dataSource) : IHandlerContext
{
    public IWindowsOs Os { get; } = os;

    public DataSourceResult DataSource { get; } = dataSource;
}
