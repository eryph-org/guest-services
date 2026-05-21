using System.Text.Json;
using Eryph.GuestServices.CloudConfig.Yaml;
using Microsoft.Extensions.Logging;
using CloudConfigNetwork = Eryph.GuestServices.CloudConfig.NetworkConfig;

namespace Eryph.GuestServices.Provisioning.DataSources;

public sealed class ConfigDriveDataSource(
    IVolumeProbe volumeProbe,
    ILogger<ConfigDriveDataSource> logger) : IDataSource
{
    private const string ExpectedLabel = "config-2";

    public string Name => "ConfigDrive";

    public int Priority => 40;

    public bool RequiresNetwork => false;

    public async Task<DataSourceProbeResult> ProbeAsync(CancellationToken cancellationToken)
    {
        var volume = volumeProbe.EnumerateVolumes()
            .FirstOrDefault(v => string.Equals(v.Label, ExpectedLabel, StringComparison.OrdinalIgnoreCase));

        if (volume is null)
            return DataSourceProbeResult.NotApplicable.Instance;

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

    public Task OnCompletedAsync(DataSourceResult data, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    internal static async Task<DataSourceResult?> ReadAsync(string root, CancellationToken cancellationToken)
    {
        var baseDir = Path.Combine(root, "openstack", "latest");
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

        string? userData = null;
        var userDataPath = Path.Combine(baseDir, "user_data");
        if (File.Exists(userDataPath))
            userData = await File.ReadAllTextAsync(userDataPath, cancellationToken).ConfigureAwait(false);

        string? vendorData = null;
        var vendorDataPath = Path.Combine(baseDir, "vendor_data.json");
        if (File.Exists(vendorDataPath))
            vendorData = await File.ReadAllTextAsync(vendorDataPath, cancellationToken).ConfigureAwait(false);

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
        };
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
