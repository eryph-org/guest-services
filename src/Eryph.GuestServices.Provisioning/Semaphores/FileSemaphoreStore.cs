using System.Text.Json;
using System.Text.Json.Serialization;
using Eryph.GuestServices.Provisioning.Stages;
using Microsoft.Extensions.Logging;

namespace Eryph.GuestServices.Provisioning.Semaphores;

/// <summary>
/// File-system semaphore store. Layout mirrors cloud-init:
/// <list type="bullet">
///   <item><c>%ProgramData%\eryph\provisioning\instance\&lt;instance-id&gt;\sem\&lt;module&gt;.per-instance</c></item>
///   <item><c>%ProgramData%\eryph\provisioning\sem\&lt;module&gt;.per-boot</c></item>
///   <item><c>%ProgramData%\eryph\provisioning\sem\&lt;module&gt;.per-once</c></item>
/// </list>
/// File contents are a JSON line with timestamp / instance / outcome for
/// post-mortem inspection; gating is done on existence alone.
/// </summary>
public sealed class FileSemaphoreStore : ISemaphoreStore
{
    private readonly ILogger<FileSemaphoreStore> _logger;
    private readonly string _root;

    public FileSemaphoreStore(ILogger<FileSemaphoreStore> logger)
        : this(logger, DefaultRoot())
    {
    }

    // Test seam: lets tests point at a temporary root without touching ProgramData.
    public FileSemaphoreStore(ILogger<FileSemaphoreStore> logger, string root)
    {
        _logger = logger;
        _root = root;
    }

    public Task<bool> ExistsAsync(
        string moduleKey,
        ModuleFrequency frequency,
        string instanceId,
        CancellationToken cancellationToken)
    {
        var path = ResolvePath(moduleKey, frequency, instanceId);
        return Task.FromResult(File.Exists(path));
    }

    public async Task<string?> ReadOutcomeAsync(
        string moduleKey,
        ModuleFrequency frequency,
        string instanceId,
        CancellationToken cancellationToken)
    {
        var path = ResolvePath(moduleKey, frequency, instanceId);
        if (!File.Exists(path))
            return null;

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            var record = JsonSerializer.Deserialize(json, SemaphoreStoreJsonContext.Default.SemaphoreRecord);
            return record?.Outcome;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // Corrupt or partial marker — treat as "exists but unknown outcome".
            // The StageRunner's gate falls back to the conservative path (re-run).
            _logger.LogWarning(ex, "Failed to read semaphore outcome from {Path}; treating as unknown", path);
            return string.Empty;
        }
    }

    public async Task WriteAsync(
        string moduleKey,
        ModuleFrequency frequency,
        string instanceId,
        string outcome,
        CancellationToken cancellationToken)
    {
        var path = ResolvePath(moduleKey, frequency, instanceId);
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);

        var payload = new SemaphoreRecord(
            DateTimeOffset.UtcNow,
            instanceId,
            outcome);

        var json = JsonSerializer.Serialize(payload, SemaphoreStoreJsonContext.Default.SemaphoreRecord);

        // Atomic write: temp file + move. File-existence semantics are what
        // gate execution, so an interrupted write that leaves only the temp
        // file is correctly treated as "not yet completed".
        var tempPath = path + ".tmp";
        await File.WriteAllTextAsync(tempPath, json, cancellationToken).ConfigureAwait(false);
        File.Move(tempPath, path, overwrite: true);

        _logger.LogDebug(
            "Wrote semaphore {Path} (module={Module} frequency={Frequency} outcome={Outcome})",
            path, moduleKey, frequency, outcome);
    }

    public Task<IReadOnlyList<string>> ListPerInstanceAsync(
        string instanceId,
        CancellationToken cancellationToken)
    {
        var dir = PerInstanceDir(instanceId);
        if (!Directory.Exists(dir))
            return Task.FromResult<IReadOnlyList<string>>([]);

        var suffix = "." + FrequencySuffix(ModuleFrequency.PerInstance);
        var modules = Directory.EnumerateFiles(dir, "*" + suffix)
            .Select(p => Path.GetFileName(p))
            // Strip the .per-instance suffix; the file name before is the module key.
            .Select(name => name.Substring(0, name.Length - suffix.Length))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        return Task.FromResult<IReadOnlyList<string>>(modules);
    }

    public Task ClearPerInstanceAsync(string instanceId, CancellationToken cancellationToken)
    {
        var dir = PerInstanceDir(instanceId);
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
            _logger.LogInformation("Cleared per-instance semaphores at {Path}", dir);
        }
        return Task.CompletedTask;
    }

    public Task ClearPerBootAsync(CancellationToken cancellationToken) =>
        ClearGlobalByFrequencyAsync(ModuleFrequency.PerBoot);

    public Task ClearPerOnceAsync(CancellationToken cancellationToken) =>
        ClearGlobalByFrequencyAsync(ModuleFrequency.PerOnce);

    private Task ClearGlobalByFrequencyAsync(ModuleFrequency frequency)
    {
        var dir = GlobalDir();
        if (!Directory.Exists(dir))
            return Task.CompletedTask;

        var suffix = "." + FrequencySuffix(frequency);
        var removed = 0;
        foreach (var file in Directory.EnumerateFiles(dir, "*" + suffix))
        {
            try
            {
                File.Delete(file);
                removed++;
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Failed to delete semaphore {Path}", file);
            }
        }

        if (removed > 0)
            _logger.LogInformation("Cleared {Count} {Frequency} semaphores under {Path}",
                removed, frequency, dir);

        return Task.CompletedTask;
    }

    private string ResolvePath(string moduleKey, ModuleFrequency frequency, string instanceId)
    {
        var fileName = moduleKey + "." + FrequencySuffix(frequency);
        return frequency switch
        {
            ModuleFrequency.PerInstance => Path.Combine(PerInstanceDir(instanceId), fileName),
            ModuleFrequency.PerBoot => Path.Combine(GlobalDir(), fileName),
            ModuleFrequency.PerOnce => Path.Combine(GlobalDir(), fileName),
            _ => throw new ArgumentOutOfRangeException(nameof(frequency), frequency, "Unknown module frequency"),
        };
    }

    private string PerInstanceDir(string instanceId) =>
        Path.Combine(_root, "instance", SanitizeForPath(instanceId), "sem");

    private string GlobalDir() => Path.Combine(_root, "sem");

    private static string FrequencySuffix(ModuleFrequency frequency) => frequency switch
    {
        ModuleFrequency.PerInstance => "per-instance",
        ModuleFrequency.PerBoot => "per-boot",
        ModuleFrequency.PerOnce => "per-once",
        _ => throw new ArgumentOutOfRangeException(nameof(frequency), frequency, "Unknown module frequency"),
    };

    private static string SanitizeForPath(string instanceId)
    {
        // Instance ids from cloud platforms are usually safe (ascii ids), but a
        // hand-rolled "--instance-id" override may contain anything. Replace
        // any path-illegal character with '_' so we can never escape the root.
        if (string.IsNullOrEmpty(instanceId))
            return "_";
        var invalid = Path.GetInvalidFileNameChars();
        var chars = instanceId.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (Array.IndexOf(invalid, chars[i]) >= 0)
                chars[i] = '_';
        }
        return new string(chars);
    }

    private static string DefaultRoot()
    {
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return Path.Combine(programData, "eryph", "provisioning");
    }

    // Stored as a JSON object so existing files round-trip cleanly even as
    // we extend the marker shape.
    internal sealed record SemaphoreRecord(
        DateTimeOffset Timestamp,
        string InstanceId,
        string Outcome);
}

[JsonSerializable(typeof(FileSemaphoreStore.SemaphoreRecord))]
[JsonSourceGenerationOptions(
    WriteIndented = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class SemaphoreStoreJsonContext : JsonSerializerContext;
