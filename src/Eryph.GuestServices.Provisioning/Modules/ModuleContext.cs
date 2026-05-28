using Eryph.GuestServices.Provisioning.DataSources;
using Eryph.GuestServices.Provisioning.Windows;

namespace Eryph.GuestServices.Provisioning.Modules;

internal sealed class ModuleContext(
    IWindowsOs os,
    DataSourceResult dataSource,
    bool isRebootResume = false) : IModuleContext
{
    public IWindowsOs Os { get; } = os;

    public DataSourceResult DataSource { get; } = dataSource;

    public bool IsRebootResume { get; } = isRebootResume;
}
