using System.Text;
using Eryph.GuestServices.CloudConfig.Yaml;
using Eryph.GuestServices.Provisioning.UserData;
using Microsoft.Extensions.Logging;
using YamlDotNet.RepresentationModel;
using CloudConfigNetwork = Eryph.GuestServices.CloudConfig.NetworkConfig;

namespace Eryph.GuestServices.Provisioning.DataSources;

public sealed class NoCloudDataSource(
    IVolumeProbe volumeProbe,
    IUrlHelper urlHelper,
    IPlatformProbe platformProbe,
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
        if (platformProbe.IsRunningOnAzure())
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

    public Task OnCompletedAsync(DataSourceResult data, CancellationToken cancellationToken)
    {
        // RFC 0005: cloud-init's NoCloud datasource doesn't unmount/eject the
        // cidata ISO either — eryph-zero deliberately keeps the drive attached
        // so a `egs-tool reset` can re-read the same payload. Cleanup here would
        // also race with the host's storage stack on Windows. No-op by design.
        return Task.CompletedTask;
    }

    internal async Task<DataSourceResult?> ReadAsync(string root, CancellationToken cancellationToken)
    {
        var metaDataPath = Path.Combine(root, "meta-data");
        if (!File.Exists(metaDataPath))
            return null;

        var rawMetaData = await File.ReadAllTextAsync(metaDataPath, cancellationToken).ConfigureAwait(false);
        var metaData = ParseMetaData(rawMetaData);

        // Read user-data and vendor-data as RAW BYTES, not as text. Real-world
        // user-data is often gzipped multipart MIME (eryph-zero's configdrive
        // ships it that way); the gzip header byte 0x8B is not valid UTF-8 and
        // ReadAllText would silently corrupt it to U+FFFD. The pipeline expects
        // exactly the bytes on disk.
        byte[]? userData = null;
        var userDataPath = Path.Combine(root, "user-data");
        if (File.Exists(userDataPath))
            userData = await File.ReadAllBytesAsync(userDataPath, cancellationToken).ConfigureAwait(false);

        byte[]? vendorData = null;
        var vendorDataPath = Path.Combine(root, "vendor-data");
        if (File.Exists(vendorDataPath))
            vendorData = await File.ReadAllBytesAsync(vendorDataPath, cancellationToken).ConfigureAwait(false);

        string? networkConfig = null;
        var networkConfigPath = Path.Combine(root, "network-config");
        if (File.Exists(networkConfigPath))
            networkConfig = await File.ReadAllTextAsync(networkConfigPath, cancellationToken).ConfigureAwait(false);

        // cloud-init's NoCloud datasource honors a `seedfrom` pointer in the
        // local meta-data. When present it points at a base URL/path from which
        // the seed documents are (re)fetched and supplement/override the local
        // ones. This is the URL form ({seedfrom}meta-data, {seedfrom}user-data,
        // …); cloud-init lets the seed's instance-id win, so the seed metadata
        // takes precedence over the local copy.
        if (metaData.TryGetValue("seedfrom", out var seedFrom) && !string.IsNullOrWhiteSpace(seedFrom))
        {
            (metaData, userData, vendorData, networkConfig) = await ApplySeedFromAsync(
                seedFrom.Trim(),
                metaData,
                userData,
                vendorData,
                networkConfig,
                cancellationToken).ConfigureAwait(false);
        }

        var instanceId = metaData.TryGetValue("instance-id", out var id) && !string.IsNullOrWhiteSpace(id)
            ? id
            : throw new InvalidDataException("NoCloud meta-data does not contain 'instance-id'.");

        metaData.TryGetValue("local-hostname", out var hostname);

        var structuredNetworkConfig = networkConfig is null ? null : TryParseNetworkConfig(networkConfig);

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

    // Fetch the seed documents pointed at by `seedfrom` and merge them over the
    // locally-present ones. Mirrors cloud-init's resilience: a seed that is
    // unreachable, or partially present, falls back to the local files instead
    // of failing the whole datasource.
    private async Task<(Dictionary<string, string> MetaData, byte[]? UserData, byte[]? VendorData, string? NetworkConfig)>
        ApplySeedFromAsync(
            string seedFrom,
            Dictionary<string, string> localMetaData,
            byte[]? localUserData,
            byte[]? localVendorData,
            string? localNetworkConfig,
            CancellationToken cancellationToken)
    {
        // cloud-init treats seedfrom as a base that it appends the document
        // names to, so it is expected to end in a separator. Be tolerant of a
        // missing trailing slash rather than silently concatenating.
        var baseUrl = seedFrom.EndsWith('/') ? seedFrom : seedFrom + "/";

        Dictionary<string, string>? seedMetaData = null;
        try
        {
            var seedMetaBytes = await urlHelper
                .FetchAsync(baseUrl + "meta-data", cancellationToken)
                .ConfigureAwait(false);
            var seedMetaText = DecodeUtf8(seedMetaBytes);
            seedMetaData = ParseMetaData(seedMetaText);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "NoCloud seedfrom '{SeedFrom}' meta-data could not be fetched; falling back to local files",
                seedFrom);
            // Seed unreachable: keep everything local exactly as it was.
            return (localMetaData, localUserData, localVendorData, localNetworkConfig);
        }

        // Loop guard: cloud-init reads seedfrom exactly once. If the fetched
        // meta-data itself carries a seedfrom, do not recurse — warn and drop it.
        if (seedMetaData.Remove("seedfrom"))
        {
            logger.LogWarning(
                "NoCloud seed meta-data from '{SeedFrom}' itself contains 'seedfrom'; ignoring to avoid recursion",
                seedFrom);
        }

        // Seed meta-data supplements/overrides the local one. Start from the
        // local map (minus the consumed seedfrom pointer) and let seed keys win,
        // matching cloud-init where the seed's instance-id is authoritative.
        var mergedMetaData = new Dictionary<string, string>(localMetaData, StringComparer.OrdinalIgnoreCase);
        mergedMetaData.Remove("seedfrom");
        foreach (var (key, value) in seedMetaData)
            mergedMetaData[key] = value;

        // user-data from the seed replaces the local copy when present.
        var userData = localUserData;
        try
        {
            userData = await urlHelper
                .FetchAsync(baseUrl + "user-data", cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogDebug(
                ex,
                "NoCloud seedfrom '{SeedFrom}' user-data not available; keeping local user-data",
                seedFrom);
        }

        // vendor-data and network-config are best-effort per cloud-init.
        var vendorData = localVendorData;
        try
        {
            vendorData = await urlHelper
                .FetchAsync(baseUrl + "vendor-data", cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogDebug(
                ex,
                "NoCloud seedfrom '{SeedFrom}' vendor-data not available; keeping local vendor-data",
                seedFrom);
        }

        var networkConfig = localNetworkConfig;
        try
        {
            var seedNetworkBytes = await urlHelper
                .FetchAsync(baseUrl + "network-config", cancellationToken)
                .ConfigureAwait(false);
            networkConfig = DecodeUtf8(seedNetworkBytes);
        }
        catch (Exception ex)
        {
            logger.LogDebug(
                ex,
                "NoCloud seedfrom '{SeedFrom}' network-config not available; keeping local network-config",
                seedFrom);
        }

        return (mergedMetaData, userData, vendorData, networkConfig);
    }

    private static string DecodeUtf8(byte[] bytes)
    {
        // meta-data / network-config are text documents. Strip a UTF-8 BOM if a
        // producer emitted one so the YAML parser sees a clean stream.
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        return Encoding.UTF8.GetString(bytes);
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

    // NoCloud meta-data is a YAML document with an open schema. Real-world
    // samples legitimately carry nested maps (e.g. `public-keys:`) and block
    // scalars (e.g. `network-interfaces: |`), so a flat `key: value` line split
    // is insufficient — it drops everything after the first colon and crashes
    // the meaning of structured values. We parse with YamlDotNet's
    // representation model (the same primitives CloudConfigYamlSerializer's
    // unknown-key walker uses) rather than a strongly-typed deserialize.
    //
    // Top-level scalar entries flatten into the existing Dictionary<string,string>
    // MetaData shape (so instance-id / local-hostname keep working exactly as
    // before). Top-level entries whose value is a map or list are preserved as
    // re-serialized YAML text — the same "store nested structure as raw text"
    // shape ConfigDriveDataSource.FlattenJson uses via GetRawText(), so both
    // datasources hand the pipeline a consistent dictionary.
    internal static Dictionary<string, string> ParseMetaData(string content)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(content))
            return result;

        YamlStream stream;
        try
        {
            stream = new YamlStream();
            stream.Load(new StringReader(content));
        }
        catch
        {
            // A meta-data document that does not parse as YAML is treated as
            // empty; the caller's missing-instance-id contract then surfaces a
            // clean InvalidDataException rather than a YamlDotNet stack trace.
            return result;
        }

        if (stream.Documents.Count == 0)
            return result;
        if (stream.Documents[0].RootNode is not YamlMappingNode root)
            return result;

        foreach (var (keyNode, valueNode) in root.Children)
        {
            if (keyNode is not YamlScalarNode keyScalar)
                continue;
            var key = keyScalar.Value;
            if (string.IsNullOrEmpty(key))
                continue;

            result[key] = valueNode switch
            {
                // Flat scalar: keep the plain value (block/literal scalars come
                // through here too, with their multi-line content intact).
                YamlScalarNode scalar => scalar.Value ?? string.Empty,
                // Map / list: preserve as re-serialized YAML text so structured
                // values are not lost (mirrors ConfigDrive's GetRawText shape).
                _ => SerializeNode(valueNode),
            };
        }

        return result;
    }

    private static string SerializeNode(YamlNode node)
    {
        var doc = new YamlDocument(node);
        var stream = new YamlStream(doc);
        using var writer = new StringWriter();
        stream.Save(writer, assignAnchors: false);
        return writer.ToString().TrimEnd('\r', '\n');
    }
}
