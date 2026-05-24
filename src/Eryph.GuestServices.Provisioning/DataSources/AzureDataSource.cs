using System.Net.Http;
using System.Runtime.Versioning;
using System.Text.Json;
using Eryph.GuestServices.CloudConfig;
using Eryph.GuestServices.Provisioning.DataSources.Azure;
using Eryph.GuestServices.Provisioning.Windows.Win32;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Eryph.GuestServices.Provisioning.DataSources;

/// <summary>
/// Azure datasource — coexists with Microsoft's Provisioning Agent (PA) and
/// the long-running Windows Guest Agent (WinGA, <c>WindowsAzureGuestAgent.exe</c>)
/// per RFC 0008 + RFC 0014. PA owns the first wireserver Ready POST during
/// OOBE; WinGA owns every Ready/heartbeat/extension/telemetry call after that
/// indefinitely. The wireserver channel is never idle on a Microsoft-Windows
/// image, so we never touch it. We run after PA completes and consume:
///
///   (a) <c>C:\AzureData\CustomData.bin</c> — PA base64-decodes the ovf-env
///       CustomData element and writes the bytes verbatim. Not encrypted at
///       any layer (verified, see docs/research/azure-customdata-encryption.md).
///       Surfaced as <see cref="DataSourceResult.UserData"/> directly.
///   (b) IMDS at <c>169.254.169.254</c> for live <c>compute.*</c> fields.
///       Distinct from wireserver (<c>168.63.129.16</c>), which we do NOT call.
///   (c) ovf-env.xml from a still-mounted ConfigDrive — fallback for cases
///       where PA hasn't yet ejected the drive (rare on Azure post-PA).
///
/// We MUST NOT signal Ready to the wireserver, post telemetry, or call any
/// other wireserver endpoint — PA + WinGA own that channel.
/// </summary>
public sealed class AzureDataSource : IDataSource
{
    internal const string AzureVmIdKey = @"SOFTWARE\Microsoft\Windows Azure";
    internal const string AzureVmIdValue = "VmId";
    internal const string CustomDataPath = @"C:\AzureData\CustomData.bin";

    // The Windows OOBE-done sentinel. Windows Setup flips ImageState to
    // IMAGE_STATE_COMPLETE when oobeSystem finishes — so on Azure this is
    // the OS-level signal that PA's specialize/oobeSystem chain has wrapped.
    // See: learn.microsoft.com/windows-hardware/manufacture/desktop/windows-setup-states
    internal const string ImageStateKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Setup\State";
    internal const string ImageStateValue = "ImageState";
    internal const string ImageStateComplete = "IMAGE_STATE_COMPLETE";

    // The Azure Windows Guest Agent service — installed Disabled on our
    // images, enabled+started by azure-detect.ps1 only on Azure, and put
    // into Running by Microsoft's PA chain after first Ready POST. So
    // "WinGA Running" is the Azure-specific PA-finished signal.
    internal const string WindowsAzureGuestAgentService = "WindowsAzureGuestAgent";

    private readonly ILogger<AzureDataSource> _logger;
    private readonly IVolumeProbe _volumeProbe;
    private readonly Func<AzureImdsClient> _imdsClientFactory;
    private readonly Func<string, bool> _fileExists;
    private readonly Func<string, CancellationToken, Task<byte[]>> _readFileBytes;
    private readonly Action<string> _deleteFile;
    private readonly Func<string, bool> _directoryExists;
    private readonly Action<string> _deleteDirectoryIfEmpty;
    private readonly Func<bool> _isRunningOnAzure;
    private readonly Func<string?> _readImageState;
    private readonly Func<string, string?> _readServiceState;
    private readonly string _customDataPath;

    /// <summary>
    /// Production constructor. Azure detection flows through the injected
    /// <see cref="IPlatformProbe"/> so the host platform of a CI agent can't
    /// flip the gate under test.
    /// </summary>
    public AzureDataSource(IVolumeProbe volumeProbe, IPlatformProbe platformProbe, ILogger<AzureDataSource> logger)
        : this(
            volumeProbe,
            logger,
            imdsClientFactory: () => new AzureImdsClient(logger),
            fileExists: File.Exists,
            readFileBytes: (p, ct) => File.ReadAllBytesAsync(p, ct),
            deleteFile: File.Delete,
            directoryExists: Directory.Exists,
            deleteDirectoryIfEmpty: DeleteDirectoryIfEmpty,
            isRunningOnAzure: platformProbe.IsRunningOnAzure,
            readImageState: ReadImageStateFromRegistry,
            readServiceState: ReadServiceStateViaCim,
            customDataPath: CustomDataPath)
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
        Func<string, CancellationToken, Task<byte[]>> readFileBytes,
        Action<string>? deleteFile = null,
        Func<string, bool>? directoryExists = null,
        Action<string>? deleteDirectoryIfEmpty = null,
        Func<bool>? isRunningOnAzure = null,
        Func<string?>? readImageState = null,
        Func<string, string?>? readServiceState = null,
        string? customDataPath = null)
    {
        _volumeProbe = volumeProbe;
        _logger = logger;
        _imdsClientFactory = imdsClientFactory;
        _fileExists = fileExists;
        _readFileBytes = readFileBytes;
        _deleteFile = deleteFile ?? File.Delete;
        _directoryExists = directoryExists ?? Directory.Exists;
        _deleteDirectoryIfEmpty = deleteDirectoryIfEmpty ?? DeleteDirectoryIfEmpty;
        _isRunningOnAzure = isRunningOnAzure ?? PlatformProbes.IsRunningOnAzure;
        _readImageState = readImageState ?? ReadImageStateFromRegistry;
        _readServiceState = readServiceState ?? ReadServiceStateViaCim;
        _customDataPath = customDataPath ?? CustomDataPath;
    }

    private static void DeleteDirectoryIfEmpty(string path)
    {
        if (!Directory.Exists(path))
            return;
        if (Directory.EnumerateFileSystemEntries(path).Any())
            return;
        Directory.Delete(path);
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

        if (!_isRunningOnAzure())
            return DataSourceProbeResult.NotApplicable.Instance;

        // Gate the read on PA being finished. Microsoft's PA writes
        // CustomData.bin during oobeSystem and only POSTs `Ready` to the
        // wireserver after its chain completes; if we run modules in
        // parallel (especially anything that triggers a reboot) we kill
        // PA mid-flight and the Azure fabric times the VM out at ~40 min.
        // Returning WaitForReady lets DataSourceLocator back off and retry
        // — see RFC 0004 for the backoff + budget. The CLI defaults give
        // us up to 15 minutes total which covers PA worst-case.
        var readiness = ProbePaReadiness();
        if (!readiness.IsReady)
        {
            _logger.LogDebug(
                "Azure: deferring datasource read — {Reason}",
                readiness.MissingSignal);
            return new DataSourceProbeResult.WaitForReady(
                readiness.MissingSignal ?? "PA has not finished provisioning",
                TimeSpan.FromSeconds(5));
        }

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

    /// <summary>
    /// Result of the three-signal PA-readiness probe. <c>IsReady</c> is true
    /// iff Windows OOBE has completed (ImageState), the Azure Windows Guest
    /// Agent service is Running (PA→WinGA handoff), and PA has written
    /// <c>C:\AzureData\CustomData.bin</c>. Any miss yields a human-readable
    /// reason for the WaitForReady the locator surfaces in its retry loop.
    /// </summary>
    internal readonly record struct PaReadinessProbe(bool IsReady, string? MissingSignal);

    internal PaReadinessProbe ProbePaReadiness()
    {
        // (1) Windows OOBE: ImageState becomes IMAGE_STATE_COMPLETE only
        // after oobeSystem exits. While PA's Unattend.wsf chain is still
        // running this stays at IMAGE_STATE_UNDEPLOYABLE / _SPECIALIZE.
        var imageState = SafeRead(_readImageState, nameof(_readImageState));
        if (imageState is null)
            return new PaReadinessProbe(false, "Windows ImageState registry value missing — OOBE still in progress.");
        if (!string.Equals(imageState, ImageStateComplete, StringComparison.OrdinalIgnoreCase))
            return new PaReadinessProbe(false,
                $"Windows OOBE not finished (ImageState='{imageState}', expected '{ImageStateComplete}').");

        // (2) PA → WinGA handoff: WinGA stays Disabled until azure-detect.ps1
        // enables it (on Azure only). PA's oobeSystem chain transitions it to
        // Running after the first Ready POST.
        string? serviceState;
        try
        {
            serviceState = _readServiceState(WindowsAzureGuestAgentService);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Azure: failed to read {Service} state; treating as not-ready", WindowsAzureGuestAgentService);
            return new PaReadinessProbe(false,
                $"Could not read state of '{WindowsAzureGuestAgentService}' service: {ex.Message}");
        }
        if (serviceState is null)
            return new PaReadinessProbe(false,
                $"Service '{WindowsAzureGuestAgentService}' is not installed on this host.");
        if (!string.Equals(serviceState, "Running", StringComparison.OrdinalIgnoreCase))
            return new PaReadinessProbe(false,
                $"'{WindowsAzureGuestAgentService}' service is '{serviceState}', not Running — PA chain has not handed off yet.");

        // (3) User-data materialised: PA decodes ovf-env CustomData and writes
        // it before exiting; once present, the bytes are stable for the rest
        // of the VM's life until our OnCompletedAsync cleanup removes them.
        if (!_fileExists(_customDataPath))
            return new PaReadinessProbe(false,
                $"'{_customDataPath}' has not been written by PA yet.");

        return new PaReadinessProbe(true, null);
    }

    private string? SafeRead(Func<string?> reader, string what)
    {
        try
        {
            return reader();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Azure: {What} threw; treating as missing", what);
            return null;
        }
    }

    public Task OnCompletedAsync(DataSourceResult data, CancellationToken cancellationToken)
    {
        // RFC 0005: cbi's AzureCustomDataService.provisioning_completed() deletes
        // CustomData.bin once provisioning succeeds, so a re-run on the same VM
        // can't accidentally re-consume stale user-data. Mirroring that here:
        // delete the file and remove the parent dir if it's left empty. PA only
        // writes the file once per instance, so a missing file on a second call
        // is the idempotent path — log Debug and continue.
        try
        {
            if (_fileExists(_customDataPath))
            {
                _deleteFile(_customDataPath);
                _logger.LogInformation(
                    "Azure: deleted {Path} after successful provisioning",
                    _customDataPath);
            }
            else
            {
                _logger.LogDebug(
                    "Azure: {Path} already absent; cleanup is a no-op",
                    _customDataPath);
            }

            var parent = Path.GetDirectoryName(_customDataPath);
            if (!string.IsNullOrEmpty(parent) && _directoryExists(parent))
            {
                _deleteDirectoryIfEmpty(parent);
            }
        }
        catch (Exception ex)
        {
            // Best-effort: provisioning has succeeded by the time we run; an IO
            // error here must not turn that into a Failed run. The StageRunner
            // also wraps OnProvisioningCompletedAsync in try/catch — this inner
            // catch keeps the logging close to the action that failed.
            _logger.LogWarning(
                ex,
                "Azure: cleanup of {Path} failed (best-effort); continuing",
                _customDataPath);
        }

        return Task.CompletedTask;
    }

    // internal for tests: lets us exercise the IMDS + CustomData composition on
    // a non-Azure CI host where the IsRunningOnAzure gate would short-circuit.
    internal async Task<DataSourceProbeResult> ReadAsync(CancellationToken cancellationToken)
    {
        // (1) CustomData.bin — raw bytes; never round-trip through ReadAllText.
        // PA already base64-decoded the ovf-env CustomData element; the bytes
        // here are exactly what the user submitted (may be gzipped multipart
        // MIME, plain #cloud-config, etc.). CustomData is not encrypted at any
        // layer — see docs/research/azure-customdata-encryption.md.
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

    private static string? ReadImageStateFromRegistry()
    {
        if (!OperatingSystem.IsWindows())
            return null;
        try
        {
            return ReadImageStateCore();
        }
        catch
        {
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static string? ReadImageStateCore()
    {
        using var key = Registry.LocalMachine.OpenSubKey(ImageStateKey);
        return key?.GetValue(ImageStateValue) as string;
    }

    private static string? ReadServiceStateViaCim(string serviceName)
    {
        if (!OperatingSystem.IsWindows())
            return null;
        return ReadServiceStateCore(serviceName);
    }

    [SupportedOSPlatform("windows")]
    private static string? ReadServiceStateCore(string serviceName) =>
        CimService.GetState(serviceName);

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
