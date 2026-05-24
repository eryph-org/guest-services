using System.Text.Json;
using Eryph.GuestServices.CloudConfig.Yaml;
using Microsoft.Extensions.Logging;
using CloudConfigNetwork = Eryph.GuestServices.CloudConfig.NetworkConfig;

namespace Eryph.GuestServices.Provisioning.DataSources;

public sealed class ConfigDriveDataSource(
    IVolumeProbe volumeProbe,
    IPlatformProbe platformProbe,
    ILogger<ConfigDriveDataSource> logger) : IDataSource
{
    private const string ExpectedLabel = "config-2";

    // cloud-init's OS_VERSIONS, chronological order
    // (cloudinit/sources/helpers/openstack.py). _find_working_version walks
    // these newest-first and uses the first one present, else "latest".
    private static readonly string[] OsVersions =
    [
        "2012-08-10",
        "2013-04-04",
        "2013-10-17",
        "2015-10-15",
        "2016-06-30",
        "2016-10-06",
        "2017-02-22",
        "2018-08-27",
    ];

    public string Name => "ConfigDrive";

    public int Priority => 40;

    public bool RequiresNetwork => false;

    public async Task<DataSourceProbeResult> ProbeAsync(CancellationToken cancellationToken)
    {
        var volume = volumeProbe.EnumerateVolumes()
            .FirstOrDefault(v => string.Equals(v.Label, ExpectedLabel, StringComparison.OrdinalIgnoreCase));

        if (volume is null)
            return DataSourceProbeResult.NotApplicable.Instance;

        // Defensive opt-out: if we're on Azure, the config-2 disk (the label
        // Azure uses for its PA delivery in some scenarios) belongs to PA, not
        // to OpenStack. AzureDataSource has higher priority but it's a stub today.
        if (platformProbe.IsRunningOnAzure())
        {
            logger.LogInformation(
                "ConfigDrive volume at {Root} found but Azure context detected; declining to claim it",
                volume.RootPath);
            return DataSourceProbeResult.NotApplicable.Instance;
        }

        logger.LogInformation("ConfigDrive datasource located volume at {Root}", volume.RootPath);

        try
        {
            var data = await ReadAsync(volume.RootPath, cancellationToken).ConfigureAwait(false);
            if (data is null)
                return DataSourceProbeResult.NotApplicable.Instance;

            return new DataSourceProbeResult.Ready(data);
        }
        catch (Exception ex)
        {
            return new DataSourceProbeResult.Failed($"ConfigDrive datasource is malformed: {ex.Message}", ex);
        }
    }

    public Task OnCompletedAsync(DataSourceResult data, CancellationToken cancellationToken)
    {
        // RFC 0005: same rationale as NoCloud — eryph-zero keeps the config-2
        // ISO attached so a `egs-tool reset` can re-read the same payload.
        // cloud-init's OpenStack ConfigDrive datasource doesn't eject the
        // volume on success either; the host owns its lifetime.
        return Task.CompletedTask;
    }

    internal static async Task<DataSourceResult?> ReadAsync(string root, CancellationToken cancellationToken)
    {
        // cloud-init _find_working_version: walk OS_VERSIONS newest-first and
        // use the first version whose meta_data.json is present; else "latest".
        // (Checking meta_data.json directly is the file-reader equivalent of
        // cloud-init listing the openstack/ directory.)
        var workingVersion = "latest";
        foreach (var version in OsVersions.Reverse())
        {
            if (File.Exists(Path.Combine(root, "openstack", version, "meta_data.json")))
            {
                workingVersion = version;
                break;
            }
        }

        var baseDir = Path.Combine(root, "openstack", workingVersion);
        var metaDataPath = Path.Combine(baseDir, "meta_data.json");
        if (!File.Exists(metaDataPath))
            return null;

        var rawMetaData = await File.ReadAllTextAsync(metaDataPath, cancellationToken).ConfigureAwait(false);
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(rawMetaData);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                $"ConfigDrive meta_data.json is not valid JSON at {metaDataPath}",
                ex);
        }

        using var _ = doc;
        var flat = FlattenJson(doc.RootElement);

        if (!flat.TryGetValue("uuid", out var instanceId) || string.IsNullOrWhiteSpace(instanceId))
            throw new InvalidDataException("ConfigDrive meta_data.json does not contain 'uuid'.");

        flat.TryGetValue("hostname", out var hostname);
        if (string.IsNullOrWhiteSpace(hostname))
            flat.TryGetValue("name", out hostname);

        flat.TryGetValue("availability_zone", out var az);

        // cloud-init OpenStack datasource get_public_ssh_keys() =
        // normalize_pubkey_data(metadata["public_keys"]) (v2 underscore key).
        var sshPublicKeys = doc.RootElement.ValueKind == JsonValueKind.Object
                            && doc.RootElement.TryGetProperty("public_keys", out var publicKeysElement)
            ? NormalizePubkeyData(publicKeysElement)
            : [];

        // Raw bytes — see NoCloudDataSource for the gzip rationale.
        byte[]? userData = null;
        var userDataPath = Path.Combine(baseDir, "user_data");
        if (File.Exists(userDataPath))
            userData = await File.ReadAllBytesAsync(userDataPath, cancellationToken).ConfigureAwait(false);

        byte[]? vendorData = null;
        var vendorDataPath = Path.Combine(baseDir, "vendor_data.json");
        if (File.Exists(vendorDataPath))
            vendorData = await File.ReadAllBytesAsync(vendorDataPath, cancellationToken).ConfigureAwait(false);

        string? networkConfig = null;
        CloudConfigNetwork? structuredNetworkConfig = null;
        var networkDataPath = Path.Combine(baseDir, "network_data.json");
        if (File.Exists(networkDataPath))
        {
            networkConfig = await File.ReadAllTextAsync(networkDataPath, cancellationToken).ConfigureAwait(false);
            // TODO: OpenStack network_data.json is JSON with a different schema than
            // cloud-init network-config; for v1 we try YAML (some sources publish
            // network-config YAML in the same slot) and ignore structured parse
            // failures so the raw text is still available downstream.
            structuredNetworkConfig = TryParseNetworkConfig(networkConfig);
        }

        return new DataSourceResult
        {
            SourceName = "ConfigDrive",
            InstanceId = instanceId,
            Hostname = string.IsNullOrWhiteSpace(hostname) ? null : hostname,
            UserData = userData,
            VendorData = vendorData,
            MetaData = flat,
            PlatformMetadata = new CloudConfig.PlatformMetadata
            {
                LocalHostname = string.IsNullOrWhiteSpace(hostname) ? null : hostname,
                AvailabilityZone = string.IsNullOrWhiteSpace(az) ? null : az,
                CloudName = "openstack",
                Platform = "openstack",
                Subplatform = "config-drive",
            },
            NetworkConfig = networkConfig,
            StructuredNetworkConfig = structuredNetworkConfig,
            SshPublicKeys = sshPublicKeys.Count > 0 ? sshPublicKeys : null,
        };
    }

    /// <summary>
    /// Clones cloud-init <c>normalize_pubkey_data</c>
    /// (cloudinit/sources/helpers/openstack.py): falsy/empty -&gt; [];
    /// string -&gt; splitlines(); list/set -&gt; the list as-is; dict -&gt; for
    /// each value, a string is treated as a single entry and a list/set
    /// contributes each non-empty entry, all flattened into one list;
    /// anything else -&gt; []. Entries are trimmed and empties dropped.
    /// </summary>
    private static List<string> NormalizePubkeyData(JsonElement pubkeyData)
    {
        var keys = new List<string>();
        switch (pubkeyData.ValueKind)
        {
            case JsonValueKind.String:
                // string -> splitlines()
                AddSplitLines(keys, pubkeyData.GetString());
                break;

            case JsonValueKind.Array:
                // list/set -> the list as-is
                foreach (var entry in pubkeyData.EnumerateArray())
                    AddTrimmed(keys, entry.ValueKind == JsonValueKind.String ? entry.GetString() : entry.GetRawText());
                break;

            case JsonValueKind.Object:
                // dict -> for each value klist: string => [klist];
                // list/set => each non-empty entry; flattened into one list.
                foreach (var property in pubkeyData.EnumerateObject())
                {
                    var klist = property.Value;
                    if (klist.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var entry in klist.EnumerateArray())
                            AddTrimmed(keys, entry.ValueKind == JsonValueKind.String ? entry.GetString() : entry.GetRawText());
                    }
                    else if (klist.ValueKind == JsonValueKind.String)
                    {
                        AddTrimmed(keys, klist.GetString());
                    }
                    // Non-string, non-list values are ignored (cloud-init only
                    // handles the string / list klist shapes).
                }

                break;

            // falsy/empty (null) and anything else -> [].
            default:
                break;
        }

        return keys;
    }

    private static void AddSplitLines(List<string> keys, string? value)
    {
        if (string.IsNullOrEmpty(value))
            return;

        foreach (var line in value.Split('\n', '\r'))
            AddTrimmed(keys, line);
    }

    private static void AddTrimmed(List<string> keys, string? value)
    {
        var trimmed = value?.Trim();
        if (!string.IsNullOrEmpty(trimmed))
            keys.Add(trimmed);
    }

    private static CloudConfigNetwork? TryParseNetworkConfig(string raw)
    {
        try
        {
            return NetworkConfigYamlSerializer.Deserialize(raw);
        }
        catch
        {
            return null;
        }
    }

    private static Dictionary<string, string> FlattenJson(JsonElement element)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (element.ValueKind != JsonValueKind.Object)
            return result;

        foreach (var property in element.EnumerateObject())
        {
            switch (property.Value.ValueKind)
            {
                case JsonValueKind.String:
                    result[property.Name] = property.Value.GetString() ?? "";
                    break;
                case JsonValueKind.Number:
                case JsonValueKind.True:
                case JsonValueKind.False:
                    result[property.Name] = property.Value.GetRawText();
                    break;
                case JsonValueKind.Null:
                    break;
                default:
                    // Keep nested structures as raw JSON so handlers can probe if needed.
                    result[property.Name] = property.Value.GetRawText();
                    break;
            }
        }

        return result;
    }
}
