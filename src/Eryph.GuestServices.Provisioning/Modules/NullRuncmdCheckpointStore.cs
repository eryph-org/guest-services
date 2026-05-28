using System.Collections.Concurrent;

namespace Eryph.GuestServices.Provisioning.Modules;

/// <summary>
/// In-memory runcmd checkpoint store used by <c>--dry-run</c>.
/// </summary>
internal sealed class NullRuncmdCheckpointStore : IRuncmdCheckpointStore
{
    private readonly ConcurrentDictionary<string, RuncmdCheckpoint> _byInstance = new(StringComparer.Ordinal);

    public Task<RuncmdCheckpoint> LoadAsync(string instanceId, CancellationToken cancellationToken) =>
        Task.FromResult(_byInstance.GetValueOrDefault(instanceId, RuncmdCheckpoint.Empty));

    public Task SaveAsync(string instanceId, RuncmdCheckpoint checkpoint, CancellationToken cancellationToken)
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
