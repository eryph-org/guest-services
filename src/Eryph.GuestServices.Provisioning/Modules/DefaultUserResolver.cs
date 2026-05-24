using Eryph.GuestServices.Provisioning.Configuration;
using Eryph.GuestServices.Provisioning.DataSources;
using Microsoft.Extensions.Logging;
using CloudConfigModel = Eryph.GuestServices.CloudConfig.CloudConfig;

namespace Eryph.GuestServices.Provisioning.Modules;

/// <summary>
/// Layered resolution of the provisioning default user. See
/// <see cref="IDefaultUserResolver"/> for the concept. The first layer that
/// yields a name wins.
/// </summary>
internal sealed class DefaultUserResolver(
    ProvisioningSettings settings,
    ILogger<DefaultUserResolver> logger) : IDefaultUserResolver
{
    public string Resolve(CloudConfigModel config, DataSourceResult dataSource)
    {
        // Layer 1: the first admin/sudo-enabled user declared in the
        // user-data. An explicitly declared admin is the operator's clearest
        // intent and always wins. Uses the shared SudoPolicy so this matches
        // UsersGroupsModule's promotion decision exactly.
        var explicitAdmin = config.Users?.FirstOrDefault(u =>
            !string.IsNullOrWhiteSpace(u.Name)
            && SudoPolicy.IsSudoEnabled(u.Sudo));
        if (explicitAdmin is not null)
        {
            logger.LogDebug("Default user resolved from cloud-config users: '{User}'.", explicitAdmin.Name);
            return explicitAdmin.Name!;
        }

        // Layer 2: a datasource-supplied default admin name. DOCUMENTED STUB
        // SEAM — DataSourceResult.DefaultUserName is always null today; it is
        // wired but inert until the ConfigDrive / OpenStack metadata work
        // (Findings 19/20) starts populating it.
        if (!string.IsNullOrWhiteSpace(dataSource.DefaultUserName))
        {
            logger.LogDebug("Default user resolved from datasource: '{User}'.", dataSource.DefaultUserName);
            return dataSource.DefaultUserName!;
        }

        // Layer 3: the image-baked default admin name from settings
        // (cloud-init system_info.default_user.name).
        if (!string.IsNullOrWhiteSpace(settings.DefaultUser.Name))
        {
            logger.LogDebug("Default user resolved from settings: '{User}'.", settings.DefaultUser.Name);
            return settings.DefaultUser.Name!;
        }

        // Layer 4: the built-in Administrator fallback. The SshModule resolves
        // the actual (possibly renamed) account by RID-500 SID when it needs
        // the on-disk name; the literal is the safe default for the shorthand
        // target.
        logger.LogDebug("Default user resolved to the 'Administrator' fallback.");
        return "Administrator";
    }
}
