using Microsoft.Extensions.Logging;

namespace Eryph.GuestServices.Provisioning.DataSources;

public sealed class NoCloudDataSource(
    IVolumeProbe volumeProbe,
    ILogger<NoCloudDataSource> logger) : IDataSource
{
    private const string ExpectedLabel = "cidata";

    public string Name => "NoCloud";

    public async Task<DataSourceResult?> TryDiscoverAsync(CancellationToken cancellationToken)
    {
        var volume = volumeProbe.EnumerateVolumes()
            .FirstOrDefault(v => string.Equals(v.Label, ExpectedLabel, StringComparison.OrdinalIgnoreCase));

        if (volume is null)
            return null;

        logger.LogInformation("NoCloud datasource located volume at {Root}", volume.RootPath);
        return await ReadAsync(volume.RootPath, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<DataSourceResult?> ReadAsync(string root, CancellationToken cancellationToken)
    {
        var metaDataPath = Path.Combine(root, "meta-data");
        if (!File.Exists(metaDataPath))
            return null;

        var rawMetaData = await File.ReadAllTextAsync(metaDataPath, cancellationToken).ConfigureAwait(false);
        var metaData = ParseYamlScalars(rawMetaData);

        var instanceId = metaData.TryGetValue("instance-id", out var id) && !string.IsNullOrWhiteSpace(id)
            ? id
            : throw new InvalidDataException("NoCloud meta-data does not contain 'instance-id'.");

        metaData.TryGetValue("local-hostname", out var hostname);

        string? userData = null;
        var userDataPath = Path.Combine(root, "user-data");
        if (File.Exists(userDataPath))
            userData = await File.ReadAllTextAsync(userDataPath, cancellationToken).ConfigureAwait(false);

        string? networkConfig = null;
        var networkConfigPath = Path.Combine(root, "network-config");
        if (File.Exists(networkConfigPath))
            networkConfig = await File.ReadAllTextAsync(networkConfigPath, cancellationToken).ConfigureAwait(false);

        return new DataSourceResult
        {
            SourceName = "NoCloud",
            InstanceId = instanceId,
            Hostname = string.IsNullOrWhiteSpace(hostname) ? null : hostname,
            UserData = userData,
            MetaData = metaData,
            NetworkConfig = networkConfig,
        };
    }

    // Tiny key: value scalar parser - NoCloud meta-data only ever uses flat scalar fields in v1.
    private static Dictionary<string, string> ParseYamlScalars(string content)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.Trim('\r', ' ', '\t');
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            var colon = line.IndexOf(':');
            if (colon <= 0)
                continue;

            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim().Trim('"', '\'');
            if (key.Length == 0)
                continue;

            result[key] = value;
        }

        return result;
    }
}
