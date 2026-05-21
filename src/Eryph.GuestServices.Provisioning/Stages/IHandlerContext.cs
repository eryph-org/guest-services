using Eryph.GuestServices.Provisioning.DataSources;
using Eryph.GuestServices.Provisioning.Windows;

namespace Eryph.GuestServices.Provisioning.Stages;

public interface IHandlerContext
{
    IWindowsOs Os { get; }

    DataSourceResult DataSource { get; }
}
