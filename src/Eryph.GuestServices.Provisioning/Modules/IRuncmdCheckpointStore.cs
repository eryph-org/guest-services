namespace Eryph.GuestServices.Provisioning.Modules;

/// <summary>
/// Persists per-entry execution state for <see cref="RuncmdModule"/> across
/// reboot-and-continue cycles. Storage layout mirrors
/// <see cref="IScriptCheckpointStore"/>:
/// <c>%ProgramData%\eryph\provisioning\instance\&lt;id&gt;\runcmd.json</c>.
/// </summary>
public interface IRuncmdCheckpointStore
{
    /// <summary>
    /// Returns the on-disk checkpoint for the given instance, or
    /// <see cref="RuncmdCheckpoint.Empty"/> when no checkpoint has ever been
    /// written. Implementations MUST tolerate a missing / corrupt file by
    /// returning <c>Empty</c> rather than throwing — a damaged file should
    /// not block a fresh run.
    /// </summary>
    Task<RuncmdCheckpoint> LoadAsync(string instanceId, CancellationToken cancellationToken);

    /// <summary>
    /// Persists the checkpoint atomically (.tmp + rename) so a torn write
    /// cannot leave stale partial state.
    /// </summary>
    Task SaveAsync(string instanceId, RuncmdCheckpoint checkpoint, CancellationToken cancellationToken);

    /// <summary>
    /// Wipes the checkpoint for the given instance. Called when the
    /// instance id changes (alongside per-instance semaphore reset).
    /// </summary>
    Task ResetAsync(string instanceId, CancellationToken cancellationToken);
}
