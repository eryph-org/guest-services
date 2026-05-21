using Eryph.GuestServices.Provisioning.DataSources;
using Eryph.GuestServices.Provisioning.Windows;

namespace Eryph.GuestServices.Provisioning.Modules;

public interface IModuleContext
{
    IWindowsOs Os { get; }

    // Exposed for modules that need raw metadata beyond the parsed CloudConfig
    // (e.g. instance id for state correlation, host-name fallback). Unused in v1.
    DataSourceResult DataSource { get; }
}
