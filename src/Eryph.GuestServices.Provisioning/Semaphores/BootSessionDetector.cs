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

    public async Task<bool> IsNewBootAsync(CancellationToken cancellationToken)
    {
        string current;
        try
        {
            current = _clock.GetCurrentBootId();
        }
        catch (Exception ex)
        {
            // If we cannot read the boot id, the safe default is "treat as a
            // new boot": per-boot modules run again. The alternative (treat
            // as same boot) would suppress modules that should have run.
            _logger.LogWarning(ex, "Failed to read current boot id; treating as new boot");
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
        File.Move(tempPath, _markerPath, overwrite: true);

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
