using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Eryph.GuestServices.Provisioning.Modules;

/// <summary>
/// File-backed user-code checkpoint store. The per-module subclass pins the
/// JSON filename (e.g. <c>runcmd.json</c>, <c>scripts.json</c>) under
/// <c>%ProgramData%\eryph\provisioning\instance\&lt;id&gt;\</c>.
/// </summary>
public abstract class FileUserCodeCheckpointStore : IUserCodeCheckpointStore
{
    private readonly ILogger _logger;
    private readonly string _root;
    private readonly string _filename;

    protected FileUserCodeCheckpointStore(ILogger logger, string filename)
        : this(logger, DefaultRoot(), filename)
    {
    }

    // Test seam.
    protected FileUserCodeCheckpointStore(ILogger logger, string root, string filename)
    {
        _logger = logger;
        _root = root;
        _filename = filename;
    }

    public async Task<UserCodeCheckpoint> LoadAsync(string instanceId, CancellationToken cancellationToken)
    {
        var path = ResolvePath(instanceId);
        if (!File.Exists(path))
            return UserCodeCheckpoint.Empty;

        try
        {
            await using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var parsed = await JsonSerializer.DeserializeAsync(
                stream,
                UserCodeCheckpointJsonContext.Default.UserCodeCheckpoint,
                cancellationToken).ConfigureAwait(false);
            return parsed ?? UserCodeCheckpoint.Empty;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            _logger.LogWarning(ex,
                "User-code checkpoint at {Path} is unreadable; starting from empty checkpoint",
                path);
            return UserCodeCheckpoint.Empty;
        }
    }

    public async Task SaveAsync(string instanceId, UserCodeCheckpoint checkpoint, CancellationToken cancellationToken)
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
                UserCodeCheckpointJsonContext.Default.UserCodeCheckpoint,
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
        Path.Combine(_root, "instance", SanitizeForPath(instanceId), _filename);

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

/// <summary>File-backed checkpoint for <see cref="RuncmdModule"/>.</summary>
public sealed class FileRuncmdCheckpointStore : FileUserCodeCheckpointStore, IRuncmdCheckpointStore
{
    public FileRuncmdCheckpointStore(ILogger<FileRuncmdCheckpointStore> logger)
        : base(logger, "runcmd.json") { }

    // Test seam.
    public FileRuncmdCheckpointStore(ILogger<FileRuncmdCheckpointStore> logger, string root)
        : base(logger, root, "runcmd.json") { }
}

/// <summary>File-backed checkpoint for <see cref="ScriptsUserModule"/>.</summary>
public sealed class FileScriptCheckpointStore : FileUserCodeCheckpointStore, IScriptCheckpointStore
{
    public FileScriptCheckpointStore(ILogger<FileScriptCheckpointStore> logger)
        : base(logger, "scripts.json") { }

    // Test seam.
    public FileScriptCheckpointStore(ILogger<FileScriptCheckpointStore> logger, string root)
        : base(logger, root, "scripts.json") { }
}
