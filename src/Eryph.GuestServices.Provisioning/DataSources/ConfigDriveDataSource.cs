using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Eryph.GuestServices.Provisioning.DataSources;

public sealed class ConfigDriveDataSource(
    IVolumeProbe volumeProbe,
    ILogger<ConfigDriveDataSource> logger) : IDataSource
{
    private const string ExpectedLabel = "config-2";

    public string Name => "ConfigDrive";

    public async Task<DataSourceResult?> TryDiscoverAsync(CancellationToken cancellationToken)
    {
        var volume = volumeProbe.EnumerateVolumes()
            .FirstOrDefault(v => string.Equals(v.Label, ExpectedLabel, StringComparison.OrdinalIgnoreCase));

        if (volume is null)
            return null;

        logger.LogInformation("ConfigDrive datasource located volume at {Root}", volume.RootPath);
        return await ReadAsync(volume.RootPath, cancellationToken).ConfigureAwait(false);
    }

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

        string? userData = null;
        var userDataPath = Path.Combine(baseDir, "user_data");
        if (File.Exists(userDataPath))
            userData = await File.ReadAllTextAsync(userDataPath, cancellationToken).ConfigureAwait(false);

        string? networkConfig = null;
        var networkDataPath = Path.Combine(baseDir, "network_data.json");
        if (File.Exists(networkDataPath))
            networkConfig = await File.ReadAllTextAsync(networkDataPath, cancellationToken).ConfigureAwait(false);

        return new DataSourceResult
        {
            SourceName = "ConfigDrive",
            InstanceId = instanceId,
            Hostname = string.IsNullOrWhiteSpace(hostname) ? null : hostname,
            UserData = userData,
            MetaData = flat,
            NetworkConfig = networkConfig,
        };
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
