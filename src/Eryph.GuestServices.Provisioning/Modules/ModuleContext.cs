using Eryph.GuestServices.Provisioning.DataSources;
using Eryph.GuestServices.Provisioning.Windows;

namespace Eryph.GuestServices.Provisioning.Modules;

internal sealed class ModuleContext(IWindowsOs os, DataSourceResult dataSource) : IModuleContext
{
    public IWindowsOs Os { get; } = os;

    public DataSourceResult DataSource { get; } = dataSource;
}
