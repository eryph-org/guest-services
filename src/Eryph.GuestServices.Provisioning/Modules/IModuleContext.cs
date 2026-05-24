using Eryph.GuestServices.Provisioning.DataSources;
using Eryph.GuestServices.Provisioning.Windows;

namespace Eryph.GuestServices.Provisioning.Modules;

public interface IModuleContext
{
    IWindowsOs Os { get; }

    /// <summary>
    /// Raw datasource metadata for modules that need more than the parsed
    /// <c>CloudConfig</c>. Consumed today by:
    /// <list type="bullet">
    ///   <item><see cref="LicensingModule"/> — checks
    ///   <c>PlatformMetadata.CloudName == "azure"</c> to skip the activation
    ///   path (Azure handles activation natively).</item>
    ///   <item><see cref="ApplyNetworkConfigModule"/> — reads
    ///   <c>StructuredNetworkConfig</c> to apply v1/v2 network-config.</item>
    ///   <item><c>ScriptsUserModule</c> — reads <c>InstanceId</c> for per-
    ///   instance checkpoint storage.</item>
    /// </list>
    /// </summary>
    DataSourceResult DataSource { get; }
}
