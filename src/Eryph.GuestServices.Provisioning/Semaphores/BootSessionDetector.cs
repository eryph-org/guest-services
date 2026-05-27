using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Eryph.GuestServices.Provisioning.Semaphores;

/// <summary>
/// Compares the current boot id (via <see cref="IBootClock"/>) with the
/// last-seen value persisted at
/// <c>%ProgramData%\eryph\provisioning\last-seen-boot.json</c>. When the
/// values differ — or no marker exists yet — the call is reported as a
/// "new boot" and the per-boot semaphores are cleared.
/// </summary>
public sealed class BootSessionDetector : IBootSessionDetector
{
    private readonly IBootClock _clock;
    private readonly ILogger<BootSessionDetector> _logger;
    private readonly string _markerPath;

    public BootSessionDetector(IBootClock clock, ILogger<BootSessionDetector> logger)
        : this(clock, logger, DefaultMarkerPath())
    {
    }

    // Test seam.
    public BootSessionDetector(IBootClock clock, ILogger<BootSessionDetector> logger, string markerPath)
    {
        _clock = clock;
        _logger = logger;
        _markerPath = markerPath;
    }

    /// <summary>
    /// Returns <c>true</c> when this run is the first observation of a new
    /// boot session (the marker is absent OR the recorded boot id differs
    /// from the current one) and <c>false</c> otherwise.
    /// <para>
    /// Failure handling: when <see cref="IBootClock.GetCurrentBootId"/>
    /// throws, the decision depends on whether the marker exists:
    /// <list type="bullet">
    ///   <item><b>Marker absent</b> → return <c>true</c>. This is the
    ///   first-ever run on the machine, so per-boot modules should run.</item>
    ///   <item><b>Marker present</b> → return <c>false</c> (fail closed).
    ///   The marker proves this isn't first boot; if we cannot determine
    ///   the current boot id we'd rather suppress per-boot modules than
    ///   re-run them every cycle on a system where the boot-id source is
    ///   chronically broken (e.g. WMI in a degraded state).</item>
    /// </list>
    /// </para>
    /// </summary>
    public async Task<bool> IsNewBootAsync(CancellationToken cancellationToken)
    {
        string current;
        try
        {
            current = _clock.GetCurrentBootId();
        }
        catch (Exception ex)
        {
            // Fail-closed when a marker exists: previous successful runs
            // recorded the boot id, so this is NOT first boot. Suppress
            // per-boot work rather than re-running it every cycle.
            // Without a marker, treat as new boot — first observation.
            if (File.Exists(_markerPath))
            {
                _logger.LogWarning(ex,
                    "Failed to read current boot id; marker exists at {Path}, treating as same boot",
                    _markerPath);
                return false;
            }

            _logger.LogWarning(ex, "Failed to read current boot id; no marker, treating as new boot");
            return true;
        }

        string? previous = null;
        if (File.Exists(_markerPath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(_markerPath, cancellationToken).ConfigureAwait(false);
                var record = JsonSerializer.Deserialize(json, BootSessionDetectorJsonContext.Default.BootMarker);
                previous = record?.BootId;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not parse {Path}; treating as new boot", _markerPath);
            }
        }

        if (string.Equals(previous, current, StringComparison.Ordinal))
        {
            _logger.LogDebug("Boot id unchanged ({BootId}); not a new boot", current);
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_markerPath)!);
        var payload = JsonSerializer.Serialize(new BootMarker(current, DateTimeOffset.UtcNow), BootSessionDetectorJsonContext.Default.BootMarker);
        var tempPath = _markerPath + ".tmp";
        await File.WriteAllTextAsync(tempPath, payload, cancellationToken).ConfigureAwait(false);
        await State.AtomicFile.ReplaceWithRetryAsync(tempPath, _markerPath, _logger, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "New boot detected: previous={Previous} current={Current}",
            previous ?? "<none>", current);
        return true;
    }

    private static string DefaultMarkerPath()
    {
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return Path.Combine(programData, "eryph", "provisioning", "last-seen-boot.json");
    }

    internal sealed record BootMarker(string BootId, DateTimeOffset DetectedAt);
}

[JsonSerializable(typeof(BootSessionDetector.BootMarker))]
[JsonSourceGenerationOptions(
    WriteIndented = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class BootSessionDetectorJsonContext : JsonSerializerContext;
