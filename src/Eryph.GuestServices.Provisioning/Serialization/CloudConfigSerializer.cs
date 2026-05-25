using Eryph.GuestServices.CloudConfig;
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

        // Acknowledged-but-no-op tier. Driven from the source-generated
        // CloudConfigPlatformInventory — every CloudConfig field tagged with
        // Platforms == Linux (i.e. no Windows analogue) and currently set on
        // the parsed config produces one Info line. This stays in lock-step
        // with the model: adding a new Linux-only field updates the log
        // surface automatically, without touching this list.
        foreach (var entry in CloudConfigPlatformInventory.Fields)
        {
            if (entry.Platforms != CloudInitPlatforms.Linux)
                continue;
            if (!entry.Present(config))
                continue;
            logger.LogInformation(
                "cloud-config: '{Key}' is acknowledged but not applied on Windows ({Reason}).",
                entry.YamlName, entry.Description);
        }

        return config;
    }
}
