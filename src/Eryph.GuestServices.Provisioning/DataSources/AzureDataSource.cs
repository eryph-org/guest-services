using System.Net.Http;
using System.Runtime.Versioning;
using System.Text.Json;
using Eryph.GuestServices.CloudConfig;
using Eryph.GuestServices.Provisioning.DataSources.Azure;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Eryph.GuestServices.Provisioning.DataSources;

/// <summary>
/// Azure datasource — coexists with Microsoft's Provisioning Agent (PA) per
/// RFC 0008 (mandatory) and RFC 0014. PA owns the wireserver Ready handshake
/// and applies HostName/AdminPassword/RDP during oobeSystem; we run after PA
/// completes and consume:
///
///   (a) <c>C:\AzureData\CustomData.bin</c> — raw bytes PA persists from
///       ovf-env.xml's CustomData element. v1 surfaces these as
///       <see cref="DataSourceResult.UserData"/> WITHOUT decryption (see
///       RFC 0015).
///   (b) IMDS instance metadata for live <c>compute.*</c> fields.
///   (c) ovf-env.xml from a still-mounted ConfigDrive — fallback for cases
///       where PA hasn't yet ejected the drive (rare on Azure post-PA).
///
/// We MUST NOT signal Ready to the wireserver — PA already did that.
/// </summary>
public sealed class AzureDataSource : IDataSource
{
    internal const string AzureVmIdKey = @"SOFTWARE\Microsoft\Windows Azure";
    internal const string AzureVmIdValue = "VmId";
    internal const string CustomDataPath = @"C:\AzureData\CustomData.bin";

    private readonly ILogger<AzureDataSource> _logger;
    private readonly IVolumeProbe _volumeProbe;
    private readonly Func<AzureImdsClient> _imdsClientFactory;
    private readonly Func<string, bool> _fileExists;
    private readonly Func<string, CancellationToken, Task<byte[]>> _readFileBytes;

    /// <summary>
    /// Production constructor.
    /// </summary>
    public AzureDataSource(IVolumeProbe volumeProbe, ILogger<AzureDataSource> logger)
        : this(
            volumeProbe,
            logger,
            imdsClientFactory: () => new AzureImdsClient(logger),
            fileExists: File.Exists,
            readFileBytes: (p, ct) => File.ReadAllBytesAsync(p, ct))
    {
    }

    /// <summary>
    /// Test seam: injectable IMDS client + file IO. The hardware-detection
    /// probes (registry / chassis) are NOT injected — tests run on a non-Azure
    /// CI host where both miss, exercising the NotApplicable branch.
    /// </summary>
    internal AzureDataSource(
        IVolumeProbe volumeProbe,
        ILogger<AzureDataSource> logger,
        Func<AzureImdsClient> imdsClientFactory,
        Func<string, bool> fileExists,
        Func<string, CancellationToken, Task<byte[]>> readFileBytes)
    {
        _volumeProbe = volumeProbe;
        _logger = logger;
        _imdsClientFactory = imdsClientFactory;
        _fileExists = fileExists;
        _readFileBytes = readFileBytes;
    }

    public string Name => "Azure";

    // Highest priority among platform-native datasources (RFC 0008/0014).
    public int Priority => 10;

    // IMDS requires the link-local network; CustomData.bin and ovf-env do not.
    // Set true so the locator does not probe us in a pre-network stage.
    public bool RequiresNetwork => true;

    public async Task<DataSourceProbeResult> ProbeAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            return DataSourceProbeResult.NotApplicable.Instance;

        if (!PlatformProbes.IsRunningOnAzure())
            return DataSourceProbeResult.NotApplicable.Instance;

        try
        {
            return await ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new DataSourceProbeResult.Failed(
                $"Azure datasource failed during read: {ex.Message}", ex);
        }
    }

    public Task OnCompletedAsync(DataSourceResult data, CancellationToken cancellationToken)
    {
        // TODO (RFC 0005): delete C:\AzureData\CustomData.bin (and empty parent
        // dir) once we have signed off on cbi-equivalent
        // AzureCustomDataService.provisioning_completed() semantics. Keeping the
        // file around in v1 is harmless — PA only writes it once per instance.
        return Task.CompletedTask;
    }

    // internal for tests: lets us exercise the IMDS + CustomData composition on
    // a non-Azure CI host where the IsRunningOnAzure gate would short-circuit.
    internal async Task<DataSourceProbeResult> ReadAsync(CancellationToken cancellationToken)
    {
        // (1) CustomData.bin — raw bytes; never round-trip through ReadAllText
        // (the file may be encrypted PKCS#7 or gzip; both have non-UTF-8 bytes).
        byte[]? userData = null;
        if (_fileExists(CustomDataPath))
        {
            userData = await _readFileBytes(CustomDataPath, cancellationToken).ConfigureAwait(false);
            _logger.LogDebug(
                "Azure: read {Bytes} bytes from {Path}",
                userData.Length, CustomDataPath);
        }

        // (2) IMDS — live instance metadata. May be null if the link-local
        // network is not (yet) up; we tolerate that and fall back to other
        // sources for the instance id.
        ImdsCompute? imdsCompute = null;
        IReadOnlyDictionary<string, string> imdsFlat = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var imds = _imdsClientFactory())
        {
            try
            {
                using var doc = await imds
                    .TryGetInstanceMetadataAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (doc is not null)
                {
                    (imdsCompute, imdsFlat) = ParseImds(doc);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Azure: IMDS query threw; continuing without it");
            }
        }

        // (3) ovf-env.xml — fallback hostname / customdata thumbprint when the
        // ConfigDrive is still mounted (rare post-PA but possible).
        var ovfEnv = TryReadOvfEnv();

        // Instance id resolution: IMDS vmId is canonical and stable. Fall back
        // to the registry VmId (PA copies it from the same source). If both
        // miss we have no provisioning identity — return Failed.
        var instanceId =
            imdsCompute?.VmId
            ?? ReadRegistryVmId();

        if (string.IsNullOrWhiteSpace(instanceId))
        {
            return new DataSourceProbeResult.Failed(
                "Azure: could not determine instance id from IMDS or registry VmId.");
        }

        // Hostname: prefer ovf-env (matches what PA actually applied if the
        // drive is present), fall back to IMDS. RFC 0008 notes the value is
        // largely informational — PA already set ComputerName.
        var hostname = ovfEnv?.Hostname ?? imdsCompute?.Name;

        var metaData = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in imdsFlat)
            metaData[k] = v;
        if (!string.IsNullOrEmpty(instanceId) && !metaData.ContainsKey("vmId"))
            metaData["vmId"] = instanceId;

        if (ovfEnv?.CustomDataCertificateThumbprint is { Length: > 0 } thumbprint)
        {
            metaData["customDataCertificateThumbprint"] = thumbprint;
            _logger.LogInformation(
                "Azure: ovf-env reports encrypted CustomData (thumbprint {Thumb}); v1 surfaces the raw bytes (RFC 0015 will decrypt)",
                thumbprint);
        }

        var result = new DataSourceResult
        {
            SourceName = "Azure",
            InstanceId = instanceId,
            Hostname = string.IsNullOrWhiteSpace(hostname) ? null : hostname,
            UserData = userData,
            VendorData = null,
            MetaData = metaData,
            PlatformMetadata = new PlatformMetadata
            {
                LocalHostname = string.IsNullOrWhiteSpace(hostname) ? null : hostname,
                Region = imdsCompute?.Location,
                AvailabilityZone = imdsCompute?.Zone,
                InstanceType = imdsCompute?.VmSize,
                CloudName = "azure",
                Platform = "azure",
                Subplatform = "customdata",
            },
            NetworkConfig = null,
        };

        return new DataSourceProbeResult.Ready(result);
    }

    private AzureOvfEnv? TryReadOvfEnv()
    {
        // Azure ConfigDrive surfaces as a CD-ROM-style volume with ovf-env.xml
        // at the root. After PA ejects it the volume is gone, which is the
        // common post-PA case. We tolerate "not found" silently.
        foreach (var volume in _volumeProbe.EnumerateVolumes())
        {
            var path = Path.Combine(volume.RootPath, "ovf-env.xml");
            if (!_fileExists(path))
                continue;

            try
            {
                var xml = File.ReadAllText(path);
                _logger.LogDebug("Azure: parsing ovf-env.xml at {Path}", path);
                return AzureOvfEnvParser.Parse(xml);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Azure: ovf-env.xml at {Path} could not be parsed; ignoring",
                    path);
                return null;
            }
        }

        return null;
    }

    private static string? ReadRegistryVmId()
    {
        if (!OperatingSystem.IsWindows())
            return null;
        try
        {
            return ReadRegistryVmIdCore();
        }
        catch
        {
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static string? ReadRegistryVmIdCore()
    {
        using var key = Registry.LocalMachine.OpenSubKey(AzureVmIdKey);
        return key?.GetValue(AzureVmIdValue) as string;
    }

    internal sealed record ImdsCompute(
        string? VmId,
        string? Name,
        string? Location,
        string? Zone,
        string? VmSize);

    internal static (ImdsCompute? Compute, IReadOnlyDictionary<string, string> Flat) ParseImds(
        JsonDocument doc)
    {
        var flat = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            return (null, flat);

        if (!doc.RootElement.TryGetProperty("compute", out var compute)
            || compute.ValueKind != JsonValueKind.Object)
        {
            return (null, flat);
        }

        foreach (var prop in compute.EnumerateObject())
        {
            switch (prop.Value.ValueKind)
            {
                case JsonValueKind.String:
                    flat[prop.Name] = prop.Value.GetString() ?? "";
                    break;
                case JsonValueKind.Number:
                case JsonValueKind.True:
                case JsonValueKind.False:
                    flat[prop.Name] = prop.Value.GetRawText();
                    break;
                case JsonValueKind.Null:
                    break;
                default:
                    flat[prop.Name] = prop.Value.GetRawText();
                    break;
            }
        }

        flat.TryGetValue("vmId", out var vmId);
        flat.TryGetValue("name", out var name);
        flat.TryGetValue("location", out var location);
        flat.TryGetValue("zone", out var zone);
        flat.TryGetValue("vmSize", out var vmSize);

        return (
            new ImdsCompute(
                NormaliseOrNull(vmId),
                NormaliseOrNull(name),
                NormaliseOrNull(location),
                NormaliseOrNull(zone),
                NormaliseOrNull(vmSize)),
            flat);
    }

    private static string? NormaliseOrNull(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s;
}
