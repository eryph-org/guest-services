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

    /// <summary>
    /// True when the StageRunner is re-entering this module after a previous
    /// run for the same instance returned
    /// <see cref="ModuleOutcome.RebootRequested"/>. Lets a module make its
    /// rename/reconfigure work one-shot: do it on the first pass, accept
    /// whatever post-reboot state the OS settled on, and complete — instead
    /// of comparing-and-retrying, which can reboot-loop when the OS
    /// normalizes the value (e.g. NetBIOS-truncates a 17-char hostname so
    /// the post-reboot <c>Environment.MachineName</c> no longer matches the
    /// requested name verbatim).
    /// </summary>
    bool IsRebootResume { get; }
}
