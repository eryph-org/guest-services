using System.Collections.Concurrent;

namespace Eryph.GuestServices.Provisioning.Modules;

/// <summary>
/// In-memory user-code checkpoint store used by <c>--dry-run</c>.
/// </summary>
internal abstract class NullUserCodeCheckpointStore : IUserCodeCheckpointStore
{
    private readonly ConcurrentDictionary<string, UserCodeCheckpoint> _byInstance = new(StringComparer.Ordinal);

    public Task<UserCodeCheckpoint> LoadAsync(string instanceId, CancellationToken cancellationToken) =>
        Task.FromResult(_byInstance.GetValueOrDefault(instanceId, UserCodeCheckpoint.Empty));

    public Task SaveAsync(string instanceId, UserCodeCheckpoint checkpoint, CancellationToken cancellationToken)
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

internal sealed class NullRuncmdCheckpointStore : NullUserCodeCheckpointStore, IRuncmdCheckpointStore { }

internal sealed class NullScriptCheckpointStore : NullUserCodeCheckpointStore, IScriptCheckpointStore { }
