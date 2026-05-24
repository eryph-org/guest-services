using Eryph.GuestServices.CloudConfig.Yaml;
using Microsoft.Extensions.Logging;
using CloudConfigModel = Eryph.GuestServices.CloudConfig.CloudConfig;

namespace Eryph.GuestServices.Provisioning.Serialization;

/// <summary>
/// Deserializes cloud-config YAML and emits a complete "what did the YAML
/// carry that the agent did not act on?" inventory at the right log levels:
///
/// - Truly unknown keys (typos, vendor extensions) → Warning. The schema
///   does not list them; most likely the operator made a mistake.
/// - Acknowledged-but-no-op keys (Linux package managers, /etc/hosts
///   management, deferred-implementation keys, future-module placeholders)
///   → Information. They parse cleanly via the schema but produce no
///   Windows behaviour. Operators need an audit trail without warning
///   noise; cross-cloud YAML routinely carries these.
///
/// Both halves live here because they are two sides of the same concern.
/// The previous implementation split tier-2 into a fake `LinuxKeysModule`,
/// which misled operators ("a module that handles Linux keys?") and pinned
/// dead per-instance semaphore state for what is purely observational
/// logging.
/// </summary>
public sealed class CloudConfigSerializer(ILogger<CloudConfigSerializer> logger) : ICloudConfigSerializer
{
    // YAML key name → (operator-visible reason, presence check against the
    // parsed POCO). Reason text is what surfaces in logs, so it explains
    // WHY this is no-op on Windows rather than just naming the key.
    private static readonly (string Key, string Reason, Func<CloudConfigModel, bool> Present)[] AcknowledgedKeys =
    [
        ("apt", "Linux APT package source configuration", c => c.Apt is not null),
        ("apt_pipelining", "Linux APT pipelining", c => c.AptPipelining is not null),
        ("packages", "Linux package list (no Windows package-manager binding yet)", c => c.Packages is not null),
        ("package_update", "Linux package-manager refresh", c => c.PackageUpdate.HasValue),
        ("package_upgrade", "Linux package-manager upgrade", c => c.PackageUpgrade.HasValue),
        ("package_reboot_if_required", "Linux post-upgrade reboot trigger", c => c.PackageRebootIfRequired.HasValue),
        ("snap", "Linux Snap configuration", c => c.Snap is not null),
        ("yum_repos", "Linux YUM repositories", c => c.YumRepos is not null),
        ("yum_repo_dir", "Linux YUM repo directory", c => !string.IsNullOrEmpty(c.YumRepoDir)),
        ("disk_setup", "Linux disk-partition setup (use 'growpart' on Windows)", c => c.DiskSetup is not null),
        ("fs_setup", "Linux filesystem-setup directives", c => c.FsSetup is not null),
        ("mounts", "Linux mount points", c => c.Mounts is not null),
        ("manage_etc_hosts", "Linux /etc/hosts management", c => c.ManageEtcHosts is not null),
        ("manage_resolv_conf", "Linux /etc/resolv.conf management", c => c.ManageResolvConf.HasValue),
        ("resolv_conf", "Linux /etc/resolv.conf contents (use 'network-config' on Windows)", c => c.ResolvConf is not null),
        ("bootcmd", "cloud-init bootcmd (not yet implemented on Windows)", c => c.Bootcmd is not null),
        ("phone_home", "cloud-init phone_home (not yet implemented)", c => c.PhoneHome is not null),
        ("final_message", "cloud-init final_message (not yet implemented)", c => !string.IsNullOrEmpty(c.FinalMessage)),
        ("ca_certs", "CA certificate installation (not yet implemented; Windows cert store differs)", c => c.CaCerts is not null),
        ("disable_root", "Linux root account management", c => c.DisableRoot.HasValue),
        ("disable_root_opts", "Linux root account management", c => !string.IsNullOrEmpty(c.DisableRootOpts)),
        ("chef", "Chef bootstrap (future)", c => c.Chef is not null),
        ("ansible", "Ansible bootstrap (future)", c => c.Ansible is not null),
        ("puppet", "Puppet bootstrap (future)", c => c.Puppet is not null),
        ("salt_minion", "Salt minion bootstrap (future)", c => c.SaltMinion is not null),
    ];

    public CloudConfigModel Deserialize(string yaml)
    {
        // The callback fires only for keys that are not on the CloudConfig
        // schema at all — neither implemented nor acknowledged-Linux-only.
        // The most likely cause is a typo (`hsotname:`) or an undocumented /
        // vendor extension. Cloud-init's runtime behaviour is to log Warning
        // and continue; we mirror that.
        var config = CloudConfigYamlSerializer.Deserialize(yaml, onUnknownKey: key =>
            logger.LogWarning(
                "cloud-config: unknown top-level key '{Key}' (ignored). " +
                "Possible typo, or a vendor / undocumented extension the " +
                "agent does not recognise.",
                key));

        // Acknowledged-but-no-op tier. These keys are real schema fields so
        // they parse cleanly above; we emit one Info line per non-null entry
        // so the operator sees the inventory of "we saw this, we did nothing".
        foreach (var (key, reason, present) in AcknowledgedKeys)
        {
            if (present(config))
                logger.LogInformation(
                    "cloud-config: '{Key}' is acknowledged but not applied on Windows ({Reason}).",
                    key, reason);
        }

        return config;
    }
}
