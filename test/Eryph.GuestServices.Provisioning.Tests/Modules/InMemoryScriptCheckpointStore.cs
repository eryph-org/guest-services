using Eryph.GuestServices.Provisioning.Modules;

namespace Eryph.GuestServices.Provisioning.Tests.Modules;

/// <summary>
/// Test seam: in-process checkpoint store that mirrors the file-backed one's
/// semantics without touching disk. Used by ScriptsUserModule tests so the
/// module can exercise the per-script resume path against a stable backing
/// store.
/// </summary>
internal sealed class InMemoryScriptCheckpointStore : IScriptCheckpointStore
{
    private readonly Dictionary<string, UserCodeCheckpoint> _byInstance = new(StringComparer.Ordinal);

    public Task<UserCodeCheckpoint> LoadAsync(string instanceId, CancellationToken cancellationToken) =>
        Task.FromResult(_byInstance.TryGetValue(instanceId, out var c) ? c : UserCodeCheckpoint.Empty);

    public Task SaveAsync(string instanceId, UserCodeCheckpoint checkpoint, CancellationToken cancellationToken)
    {
        _byInstance[instanceId] = checkpoint;
        return Task.CompletedTask;
    }

    public Task ResetAsync(string instanceId, CancellationToken cancellationToken)
    {
        _byInstance.Remove(instanceId);
        return Task.CompletedTask;
    }
}
