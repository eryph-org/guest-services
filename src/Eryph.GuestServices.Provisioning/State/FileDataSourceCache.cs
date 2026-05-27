using System.Text.Json;
using System.Text.Json.Serialization;
using Eryph.GuestServices.CloudConfig;
using Eryph.GuestServices.CloudConfig.Yaml;
using Eryph.GuestServices.Provisioning.Cli;
using Eryph.GuestServices.Provisioning.DataSources;
using Microsoft.Extensions.Logging;

namespace Eryph.GuestServices.Provisioning.State;

/// <summary>
/// File-backed <see cref="IDataSourceCache"/>: persists the located datasource as
/// <c>datasource.json</c> next to <c>state.json</c>. byte[] payloads
/// (user-data / vendor-data) are stored base64; the structured network-config is
/// not persisted but re-derived from the raw text on load (matching the reader),
/// so the cache stays a small, schema-stable JSON document.
/// </summary>
public sealed class FileDataSourceCache(ILogger<FileDataSourceCache> logger) : IDataSourceCache
{
    private readonly string _directory = ProvisioningPaths.Root;

    // Override for tests.
    public FileDataSourceCache(ILogger<FileDataSourceCache> logger, string directory)
        : this(logger)
    {
        _directory = directory;
    }

    private string CachePath => Path.Combine(_directory, "datasource.json");

    public async Task<DataSourceResult?> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(CachePath))
            return null;

        try
        {
            await using var stream = File.Open(CachePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var dto = await JsonSerializer
                .DeserializeAsync(stream, DataSourceCacheJsonContext.Default.CachedDataSource, cancellationToken)
                .ConfigureAwait(false);
            return dto is null ? null : ToResult(dto);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // The cache is an optimization; a corrupt or unreadable file (incl.
            // transient sharing/permission errors) must not abort provisioning —
            // treat it as absent and let the run fall back to a fresh locate.
            logger.LogWarning(ex, "Datasource cache at {Path} is unreadable; treating as absent", CachePath);
            return null;
        }
    }

    public async Task SaveAsync(DataSourceResult data, CancellationToken cancellationToken)
    {
        var tempPath = CachePath + ".tmp";
        try
        {
            Directory.CreateDirectory(_directory);
            await using (var stream = File.Open(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer
                    .SerializeAsync(stream, ToDto(data), DataSourceCacheJsonContext.Default.CachedDataSource, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            // The atomic replace can transiently fail with UnauthorizedAccessException
            // / IOException when antivirus scans the just-written file or a reader
            // briefly holds the destination — retry before giving up.
            await ReplaceWithRetryAsync(tempPath, CachePath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort: a cache write failure must not fail the run. The next
            // boot simply re-locates the datasource instead of restoring.
            logger.LogWarning(ex, "Failed to persist datasource cache to {Path}; continuing without it", CachePath);
            TryDelete(tempPath);
        }
    }

    public Task ResetAsync(CancellationToken cancellationToken)
    {
        TryDelete(CachePath);
        return Task.CompletedTask;
    }

    private async Task ReplaceWithRetryAsync(string source, string destination, CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromMilliseconds(50);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(source, destination, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 6)
            {
                logger.LogDebug(
                    ex, "Replacing {Destination} failed (attempt {Attempt}); retrying in {Delay}ms",
                    destination, attempt, (int)delay.TotalMilliseconds);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 1000));
            }
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Failed to delete {Path}", path);
        }
    }

    private static CachedDataSource ToDto(DataSourceResult d) => new()
    {
        SourceName = d.SourceName,
        InstanceId = d.InstanceId,
        Hostname = d.Hostname,
        DefaultUserName = d.DefaultUserName,
        UserData = d.UserData,
        VendorData = d.VendorData,
        MetaData = new Dictionary<string, string>(d.MetaData, StringComparer.Ordinal),
        PlatformMetadata = d.PlatformMetadata is null ? null : new CachedPlatformMetadata
        {
            LocalHostname = d.PlatformMetadata.LocalHostname,
            PublicKeys = d.PlatformMetadata.PublicKeys?.ToList(),
            AvailabilityZone = d.PlatformMetadata.AvailabilityZone,
            Region = d.PlatformMetadata.Region,
            CloudName = d.PlatformMetadata.CloudName,
            Platform = d.PlatformMetadata.Platform,
            Subplatform = d.PlatformMetadata.Subplatform,
            InstanceType = d.PlatformMetadata.InstanceType,
        },
        NetworkConfig = d.NetworkConfig,
        SshPublicKeys = d.SshPublicKeys?.ToList(),
    };

    private static DataSourceResult ToResult(CachedDataSource c) => new()
    {
        SourceName = c.SourceName,
        InstanceId = c.InstanceId,
        Hostname = c.Hostname,
        DefaultUserName = c.DefaultUserName,
        UserData = c.UserData,
        VendorData = c.VendorData,
        MetaData = c.MetaData,
        PlatformMetadata = c.PlatformMetadata is null ? null : new PlatformMetadata
        {
            LocalHostname = c.PlatformMetadata.LocalHostname,
            PublicKeys = c.PlatformMetadata.PublicKeys,
            AvailabilityZone = c.PlatformMetadata.AvailabilityZone,
            Region = c.PlatformMetadata.Region,
            CloudName = c.PlatformMetadata.CloudName,
            Platform = c.PlatformMetadata.Platform,
            Subplatform = c.PlatformMetadata.Subplatform,
            InstanceType = c.PlatformMetadata.InstanceType,
        },
        NetworkConfig = c.NetworkConfig,
        StructuredNetworkConfig = ReparseNetworkConfig(c.NetworkConfig),
        SshPublicKeys = c.SshPublicKeys,
    };

    // Same best-effort parse the readers use: some sources publish YAML
    // network-config in the slot, others (OpenStack network_data.json) publish a
    // JSON schema we don't structure on Windows yet — ignore parse failures.
    private static NetworkConfig? ReparseNetworkConfig(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        try { return NetworkConfigYamlSerializer.Deserialize(raw); }
        catch { return null; }
    }
}

internal sealed class CachedDataSource
{
    public string SourceName { get; set; } = "";
    public string InstanceId { get; set; } = "";
    public string? Hostname { get; set; }
    public string? DefaultUserName { get; set; }
    public byte[]? UserData { get; set; }
    public byte[]? VendorData { get; set; }
    public Dictionary<string, string> MetaData { get; set; } = new(StringComparer.Ordinal);
    public CachedPlatformMetadata? PlatformMetadata { get; set; }
    public string? NetworkConfig { get; set; }
    public List<string>? SshPublicKeys { get; set; }
}

internal sealed class CachedPlatformMetadata
{
    public string? LocalHostname { get; set; }
    public List<string>? PublicKeys { get; set; }
    public string? AvailabilityZone { get; set; }
    public string? Region { get; set; }
    public string? CloudName { get; set; }
    public string? Platform { get; set; }
    public string? Subplatform { get; set; }
    public string? InstanceType { get; set; }
}

[JsonSerializable(typeof(CachedDataSource))]
[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class DataSourceCacheJsonContext : JsonSerializerContext;
