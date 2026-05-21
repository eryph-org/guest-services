using Eryph.GuestServices.CloudConfig.Yaml;
using Microsoft.Extensions.Logging;
using CloudConfigNetwork = Eryph.GuestServices.CloudConfig.NetworkConfig;

namespace Eryph.GuestServices.Provisioning.DataSources;

public sealed class NoCloudDataSource(
    IVolumeProbe volumeProbe,
    ILogger<NoCloudDataSource> logger) : IDataSource
{
    private const string ExpectedLabel = "cidata";

    public string Name => "NoCloud";

    public int Priority => 30;

    public bool RequiresNetwork => false;

    public async Task<DataSourceProbeResult> ProbeAsync(CancellationToken cancellationToken)
    {
        var volume = volumeProbe.EnumerateVolumes()
            .FirstOrDefault(v => string.Equals(v.Label, ExpectedLabel, StringComparison.OrdinalIgnoreCase));

        if (volume is null)
            return DataSourceProbeResult.NotApplicable.Instance;

        // Defensive opt-out: if we're on Azure, the cidata-shaped disk (if any
        // ever appears) is not ours to claim — Azure's PA owns the platform.
        // The AzureDataSource has higher priority but it's still a stub today.
        if (PlatformProbes.IsRunningOnAzure())
        {
            logger.LogInformation(
                "NoCloud volume at {Root} found but Azure context detected; declining to claim it",
                volume.RootPath);
            return DataSourceProbeResult.NotApplicable.Instance;
        }

        logger.LogInformation("NoCloud datasource located volume at {Root}", volume.RootPath);

        try
        {
            var data = await ReadAsync(volume.RootPath, cancellationToken).ConfigureAwait(false);
            if (data is null)
                return DataSourceProbeResult.NotApplicable.Instance;

            return new DataSourceProbeResult.Ready(data);
        }
        catch (Exception ex)
        {
            return new DataSourceProbeResult.Failed($"NoCloud datasource is malformed: {ex.Message}", ex);
        }
    }

    public Task OnCompletedAsync(DataSourceResult data, CancellationToken cancellationToken) =>
        Task.CompletedTask;

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

        string? vendorData = null;
        var vendorDataPath = Path.Combine(root, "vendor-data");
        if (File.Exists(vendorDataPath))
            vendorData = await File.ReadAllTextAsync(vendorDataPath, cancellationToken).ConfigureAwait(false);

        string? networkConfig = null;
        CloudConfigNetwork? structuredNetworkConfig = null;
        var networkConfigPath = Path.Combine(root, "network-config");
        if (File.Exists(networkConfigPath))
        {
            networkConfig = await File.ReadAllTextAsync(networkConfigPath, cancellationToken).ConfigureAwait(false);
            structuredNetworkConfig = TryParseNetworkConfig(networkConfig);
        }

        return new DataSourceResult
        {
            SourceName = "NoCloud",
            InstanceId = instanceId,
            Hostname = string.IsNullOrWhiteSpace(hostname) ? null : hostname,
            UserData = userData,
            VendorData = vendorData,
            MetaData = metaData,
            PlatformMetadata = new CloudConfig.PlatformMetadata
            {
                LocalHostname = string.IsNullOrWhiteSpace(hostname) ? null : hostname,
                CloudName = "nocloud",
                Platform = "nocloud",
            },
            NetworkConfig = networkConfig,
            StructuredNetworkConfig = structuredNetworkConfig,
        };
    }

    private static CloudConfigNetwork? TryParseNetworkConfig(string yaml)
    {
        try
        {
            return NetworkConfigYamlSerializer.Deserialize(yaml);
        }
        catch
        {
            // The locator logs probe failures; a malformed network-config inside an
            // otherwise valid NoCloud volume is not fatal for discovery.
            return null;
        }
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
