using System.Collections.Concurrent;
using Eryph.GuestServices.Provisioning.Stages;

namespace Eryph.GuestServices.Provisioning.Semaphores;

/// <summary>
/// In-memory semaphore store used by <c>--dry-run</c>. Tracks completion
/// for the duration of the current process so the StageRunner can observe
/// its own progress, but never writes to disk. Mirrors the
/// <see cref="State.NullStateStore"/> pattern.
/// </summary>
internal sealed class NullSemaphoreStore : ISemaphoreStore
{
    private readonly ConcurrentDictionary<string, string> _markers = new(StringComparer.Ordinal);

    public Task<bool> ExistsAsync(string moduleKey, ModuleFrequency frequency, string instanceId, CancellationToken cancellationToken) =>
        Task.FromResult(_markers.ContainsKey(Key(moduleKey, frequency, instanceId)));

    public Task<string?> ReadOutcomeAsync(string moduleKey, ModuleFrequency frequency, string instanceId, CancellationToken cancellationToken)
    {
        _markers.TryGetValue(Key(moduleKey, frequency, instanceId), out var outcome);
        return Task.FromResult<string?>(outcome);
    }

    public Task WriteAsync(string moduleKey, ModuleFrequency frequency, string instanceId, string outcome, CancellationToken cancellationToken)
    {
        _markers[Key(moduleKey, frequency, instanceId)] = outcome;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> ListPerInstanceAsync(string instanceId, CancellationToken cancellationToken)
    {
        var prefix = $"{ModuleFrequency.PerInstance}:{instanceId}:";
        var items = _markers.Keys
            .Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
            .Select(k => k[prefix.Length..])
            .ToList();
        return Task.FromResult<IReadOnlyList<string>>(items);
    }

    public Task ClearPerInstanceAsync(string instanceId, CancellationToken cancellationToken)
    {
        var prefix = $"{ModuleFrequency.PerInstance}:{instanceId}:";
        foreach (var k in _markers.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
            _markers.TryRemove(k, out _);
        return Task.CompletedTask;
    }

    public Task ClearPerBootAsync(CancellationToken cancellationToken)
    {
        var prefix = $"{ModuleFrequency.PerBoot}:";
        foreach (var k in _markers.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
            _markers.TryRemove(k, out _);
        return Task.CompletedTask;
    }

    public Task ClearPerOnceAsync(CancellationToken cancellationToken)
    {
        var prefix = $"{ModuleFrequency.PerOnce}:";
        foreach (var k in _markers.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
            _markers.TryRemove(k, out _);
        return Task.CompletedTask;
    }

    private static string Key(string moduleKey, ModuleFrequency frequency, string instanceId) =>
        frequency == ModuleFrequency.PerInstance
            ? $"{frequency}:{instanceId}:{moduleKey}"
            : $"{frequency}::{moduleKey}";
}
