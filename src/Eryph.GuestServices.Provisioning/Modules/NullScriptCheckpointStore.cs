using System.Collections.Concurrent;

namespace Eryph.GuestServices.Provisioning.Modules;

/// <summary>
/// In-memory checkpoint store used by <c>--dry-run</c>. Tracks executed
/// scripts for the duration of the current process so a dry-run reflects
/// "what would resume look like" without touching disk.
/// </summary>
internal sealed class NullScriptCheckpointStore : IScriptCheckpointStore
{
    private readonly ConcurrentDictionary<string, ScriptCheckpoint> _byInstance = new(StringComparer.Ordinal);

    public Task<ScriptCheckpoint> LoadAsync(string instanceId, CancellationToken cancellationToken) =>
        Task.FromResult(_byInstance.GetValueOrDefault(instanceId, ScriptCheckpoint.Empty));

    public Task SaveAsync(string instanceId, ScriptCheckpoint checkpoint, CancellationToken cancellationToken)
    {
        _byInstance[instanceId] = checkpoint;
        return Task.CompletedTask;
    }

    public Task ResetAsync(string instanceId, CancellationToken cancellationToken)
    {
        _byInstance.TryRemove(instanceId, out _);
        return Task.CompletedTask;
    }
}
