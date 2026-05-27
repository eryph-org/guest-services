using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Eryph.GuestServices.Provisioning.Modules;

/// <summary>
/// File-backed checkpoint store. Layout mirrors the semaphore store —
/// scripts.json lives next to the per-instance sem/ directory.
/// </summary>
public sealed class FileScriptCheckpointStore : IScriptCheckpointStore
{
    private readonly ILogger<FileScriptCheckpointStore> _logger;
    private readonly string _root;

    public FileScriptCheckpointStore(ILogger<FileScriptCheckpointStore> logger)
        : this(logger, DefaultRoot())
    {
    }

    // Test seam.
    public FileScriptCheckpointStore(ILogger<FileScriptCheckpointStore> logger, string root)
    {
        _logger = logger;
        _root = root;
    }

    public async Task<ScriptCheckpoint> LoadAsync(string instanceId, CancellationToken cancellationToken)
    {
        var path = ResolvePath(instanceId);
        if (!File.Exists(path))
            return ScriptCheckpoint.Empty;

        try
        {
            await using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var parsed = await JsonSerializer.DeserializeAsync(
                stream,
                ScriptCheckpointJsonContext.Default.ScriptCheckpoint,
                cancellationToken).ConfigureAwait(false);
            return parsed ?? ScriptCheckpoint.Empty;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            _logger.LogWarning(ex,
                "Script checkpoint at {Path} is unreadable; starting from empty checkpoint",
                path);
            return ScriptCheckpoint.Empty;
        }
    }

    public async Task SaveAsync(string instanceId, ScriptCheckpoint checkpoint, CancellationToken cancellationToken)
    {
        var path = ResolvePath(instanceId);
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);

        var tempPath = path + ".tmp";
        await using (var stream = File.Open(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(
                stream,
                checkpoint,
                ScriptCheckpointJsonContext.Default.ScriptCheckpoint,
                cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        await State.AtomicFile.ReplaceWithRetryAsync(tempPath, path, _logger, cancellationToken).ConfigureAwait(false);
    }

    public Task ResetAsync(string instanceId, CancellationToken cancellationToken)
    {
        var path = ResolvePath(instanceId);
        if (File.Exists(path))
            File.Delete(path);
        return Task.CompletedTask;
    }

    private string ResolvePath(string instanceId) =>
        Path.Combine(_root, "instance", SanitizeForPath(instanceId), "scripts.json");

    private static string SanitizeForPath(string instanceId)
    {
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
}
