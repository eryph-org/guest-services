using Eryph.GuestServices.Provisioning.DataSources;
using CloudConfigModel = Eryph.GuestServices.CloudConfig.CloudConfig;

namespace Eryph.GuestServices.Provisioning.Modules;

/// <summary>
/// Resolves the account that top-level cloud-config shorthands
/// (<c>ssh_authorized_keys</c>, <c>password</c>, the <c>chpasswd</c> shorthand)
/// target when they do not name a user explicitly. This is the configurable
/// "default user" concept, the eryph analogue of cloud-init's
/// <c>system_info.default_user</c>. It is SEPARATE from the OS-level built-in
/// Administrator that <c>disable_root</c> targets. See RFC 0018.
/// </summary>
public interface IDefaultUserResolver
{
    /// <summary>
    /// Returns the name of the account top-level shorthands should target.
    /// Never returns null/empty — the final layer is an <c>"Administrator"</c>
    /// fallback.
    /// </summary>
    string Resolve(CloudConfigModel config, DataSourceResult dataSource);
}
