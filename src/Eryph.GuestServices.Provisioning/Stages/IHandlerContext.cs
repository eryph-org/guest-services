using Eryph.GuestServices.Provisioning.DataSources;
using Eryph.GuestServices.Provisioning.Windows;

namespace Eryph.GuestServices.Provisioning.Stages;

public interface IHandlerContext
{
    IWindowsOs Os { get; }

    // Exposed for handlers that need raw metadata beyond the parsed CloudConfig
    // (e.g. instance id for state correlation, host-name fallback). Unused in v1.
    DataSourceResult DataSource { get; }
}
