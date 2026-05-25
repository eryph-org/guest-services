using Eryph.GuestServices.Provisioning.Stages;

namespace Eryph.GuestServices.Provisioning.Semaphores;

/// <summary>
/// Tracks per-module completion semaphores, mirroring cloud-init's
/// <c>/var/lib/cloud/instance/sem/&lt;module&gt;.&lt;freq&gt;</c> and
/// <c>/var/lib/cloud/sem/&lt;module&gt;.&lt;freq&gt;</c> layout.
/// </summary>
public interface ISemaphoreStore
{
    /// <summary>
    /// Returns <c>true</c> when a marker exists for (module, frequency,
    /// instance) — meaning the module has been recorded in some terminal-ish
    /// state. Callers that need to distinguish <c>"completed"</c> from
    /// <c>"reboot-requested"</c> must use <see cref="ReadOutcomeAsync"/>.
    /// </summary>
    Task<bool> ExistsAsync(
        string moduleKey,
        ModuleFrequency frequency,
        string instanceId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Returns the outcome string written by the most recent
    /// <see cref="WriteAsync"/>, or <c>null</c> when no marker exists for
    /// (module, frequency, instance). The StageRunner uses this to skip on
    /// <c>"completed"</c> and resume on <c>"reboot-requested"</c>.
    /// docs/bugs/0001 documents the bug this method was introduced to fix.
    /// </summary>
    Task<string?> ReadOutcomeAsync(
        string moduleKey,
        ModuleFrequency frequency,
        string instanceId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Writes the marker file. Must be called BEFORE returning from a
    /// reboot-and-continue path, so the post-reboot pass does not re-run the
    /// module.
    /// </summary>
    Task WriteAsync(
        string moduleKey,
        ModuleFrequency frequency,
        string instanceId,
        string outcome,
        CancellationToken cancellationToken);

    /// <summary>
    /// Lists the module keys that have a per-instance marker for the given
    /// instance. Used to project the <c>CompletedHandlers</c> view back into
    /// <c>state.json</c> for compatibility with v1 callers.
    /// </summary>
    Task<IReadOnlyList<string>> ListPerInstanceAsync(
        string instanceId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Clears all per-instance markers for the given instance. Called when
    /// the instance-id changes (matches cloud-init wiping the instance
    /// directory between deploys).
    /// </summary>
    Task ClearPerInstanceAsync(string instanceId, CancellationToken cancellationToken);

    /// <summary>
    /// Clears all per-boot markers. Called at the start of every new boot
    /// session (see <c>IBootSessionDetector</c>).
    /// </summary>
    Task ClearPerBootAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Clears all per-once markers. Only invoked by an explicit operator
    /// action — <c>egs-tool reset --reset-once</c>.
    /// </summary>
    Task ClearPerOnceAsync(CancellationToken cancellationToken);
}
