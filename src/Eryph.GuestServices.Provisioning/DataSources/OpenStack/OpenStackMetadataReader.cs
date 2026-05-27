using System.Text;
using System.Text.Json;
using Eryph.GuestServices.CloudConfig.Yaml;
using CloudConfigNetwork = Eryph.GuestServices.CloudConfig.NetworkConfig;

namespace Eryph.GuestServices.Provisioning.DataSources.OpenStack;

/// <summary>
/// Shared OpenStack metadata reader used by both <see cref="ConfigDriveDataSource"/>
/// (disk) and the metadata-service (HTTP) datasource. Walks the version
/// directories newest-first, reads <c>meta_data.json</c> + the optional
/// <c>user_data</c> / <c>vendor_data.json</c> / <c>network_data.json</c>, and
/// assembles a <see cref="DataSourceResult"/>.
///
/// cloud-init parity: <c>helpers/openstack.py</c> <c>BaseReader.read_v2</c> +
/// <c>_find_working_version</c> + <c>normalize_pubkey_data</c>.
/// </summary>
internal static class OpenStackMetadataReader
{
    // cloud-init's OS_VERSIONS, chronological order
    // (cloudinit/sources/helpers/openstack.py). _find_working_version walks
    // these newest-first and uses the first one present, else "latest".
    // Verified against cloud-init main: the newest entry is OS_ROCKY (2018-08-27).
    public static readonly string[] OsVersions =
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

    private const string LatestVersion = "latest";

    /// <summary>
    /// Reads and parses the OpenStack v2 metadata tree via
    /// <paramref name="transport"/>. Returns <c>null</c> when no selectable
    /// <c>meta_data.json</c> is present (datasource not applicable). Throws
    /// <see cref="InvalidDataException"/> when a located <c>meta_data.json</c> is
    /// malformed or lacks <c>uuid</c>.
    /// </summary>
    /// <param name="transport">Reads files under the OpenStack metadata tree
    /// (file-backed for ConfigDrive, HTTP-backed for the metadata service).</param>
    /// <param name="sourceName">Value for <see cref="DataSourceResult.SourceName"/>.</param>
    /// <param name="subplatform">Value for <c>PlatformMetadata.Subplatform</c>
    /// (e.g. <c>config-drive</c> or <c>metadata-service</c>).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task<DataSourceResult?> ReadAsync(
        IOpenStackMetadataTransport transport,
        string sourceName,
        string subplatform,
        CancellationToken cancellationToken)
    {
        // _find_working_version: walk OS_VERSIONS newest-first and use the first
        // version whose meta_data.json is present; else "latest". Reading the
        // file directly is the transport-agnostic equivalent of cloud-init
        // listing the openstack/ directory.
        string? workingVersion = null;
        byte[]? metaDataBytes = null;
        foreach (var version in OsVersions.Reverse())
        {
            var bytes = await transport
                .TryReadAsync(MetaDataPath(version), cancellationToken)
                .ConfigureAwait(false);
            if (bytes is not null)
            {
                workingVersion = version;
                metaDataBytes = bytes;
                break;
            }
        }

        if (workingVersion is null)
        {
            metaDataBytes = await transport
                .TryReadAsync(MetaDataPath(LatestVersion), cancellationToken)
                .ConfigureAwait(false);
            if (metaDataBytes is null)
                return null;
            workingVersion = LatestVersion;
        }

        var metaDataPath = MetaDataPath(workingVersion);
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(metaDataBytes!);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                $"{sourceName} meta_data.json is not valid JSON at {transport.Describe(metaDataPath)}",
                ex);
        }

        using var _ = doc;
        var flat = FlattenJson(doc.RootElement);

        if (!flat.TryGetValue("uuid", out var instanceId) || string.IsNullOrWhiteSpace(instanceId))
            throw new InvalidDataException($"{sourceName} meta_data.json does not contain 'uuid'.");

        flat.TryGetValue("hostname", out var hostname);
        if (string.IsNullOrWhiteSpace(hostname))
            flat.TryGetValue("name", out hostname);

        flat.TryGetValue("availability_zone", out var az);

        // cloud-init OpenStack get_public_ssh_keys() =
        // normalize_pubkey_data(metadata["public_keys"]) (v2 underscore key).
        var sshPublicKeys = doc.RootElement.ValueKind == JsonValueKind.Object
                            && doc.RootElement.TryGetProperty("public_keys", out var publicKeysElement)
            ? NormalizePubkeyData(publicKeysElement)
            : [];

        // Raw bytes — see NoCloudDataSource for the gzip rationale.
        var userData = await transport
            .TryReadAsync(VersionPath(workingVersion, "user_data"), cancellationToken)
            .ConfigureAwait(false);

        // OpenStack vendor_data.json is a JSON blob; cloud-init convert_vendordata
        // extracts the runnable payload (a "cloud-init" key, or a bare JSON string).
        // An arbitrary metadata object/array carries no runnable vendor cloud-config.
        var vendorDataJson = await transport
            .TryReadAsync(VersionPath(workingVersion, "vendor_data.json"), cancellationToken)
            .ConfigureAwait(false);
        var vendorData = ConvertVendorData(vendorDataJson);

        string? networkConfig = null;
        CloudConfigNetwork? structuredNetworkConfig = null;
        var networkDataBytes = await transport
            .TryReadAsync(VersionPath(workingVersion, "network_data.json"), cancellationToken)
            .ConfigureAwait(false);
        if (networkDataBytes is not null)
        {
            networkConfig = Encoding.UTF8.GetString(networkDataBytes);
            // TODO: OpenStack network_data.json is JSON with a different schema than
            // cloud-init network-config; for v1 we try YAML (some sources publish
            // network-config YAML in the same slot) and ignore structured parse
            // failures so the raw text is still available downstream.
            structuredNetworkConfig = TryParseNetworkConfig(networkConfig);
        }

        return new DataSourceResult
        {
            SourceName = sourceName,
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
                Subplatform = subplatform,
            },
            NetworkConfig = networkConfig,
            StructuredNetworkConfig = structuredNetworkConfig,
            SshPublicKeys = sshPublicKeys.Count > 0 ? sshPublicKeys : null,
        };
    }

    /// <summary>
    /// Clones cloud-init <c>convert_vendordata</c> (cloudinit/sources/__init__.py)
    /// for the OpenStack <c>vendor_data.json</c> blob: a bare JSON string is the
    /// payload as-is; a JSON object exposes its runnable payload under a
    /// <c>cloud-init</c> string key; anything else (an arbitrary metadata
    /// object/array, or empty) carries no runnable vendor cloud-config and yields
    /// <c>null</c>. Non-JSON content is passed through verbatim so a producer that
    /// drops raw cloud-config in the slot still works — the user-data pipeline
    /// sniffs for a cloud-init marker regardless and ignores unmarked payloads.
    /// </summary>
    private static byte[]? ConvertVendorData(byte[]? raw)
    {
        if (raw is null || raw.Length == 0)
            return null;

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(raw);
        }
        catch (JsonException)
        {
            return raw;
        }

        using (doc)
        {
            switch (doc.RootElement.ValueKind)
            {
                case JsonValueKind.String:
                    var s = doc.RootElement.GetString();
                    return string.IsNullOrEmpty(s) ? null : Encoding.UTF8.GetBytes(s);

                case JsonValueKind.Object
                    when doc.RootElement.TryGetProperty("cloud-init", out var ci)
                         && ci.ValueKind == JsonValueKind.String:
                    var c = ci.GetString();
                    return string.IsNullOrEmpty(c) ? null : Encoding.UTF8.GetBytes(c);

                default:
                    return null;
            }
        }
    }

    private static string MetaDataPath(string version) => VersionPath(version, "meta_data.json");

    private static string VersionPath(string version, string file) => $"openstack/{version}/{file}";

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
