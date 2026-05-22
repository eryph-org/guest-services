namespace Eryph.GuestServices.Provisioning.Modules;

/// <summary>
/// Persists per-script execution state for <see cref="ScriptsUserModule"/>
/// across reboot-and-continue cycles. A script is identified by (ordinal,
/// body-hash) so an operator edit that keeps the ordinal but changes the
/// body re-runs cleanly.
///
/// Storage layout: <c>%ProgramData%\eryph\provisioning\instance\&lt;id&gt;\scripts.json</c>.
/// See docs/bugs/0001-scriptsusermodule-skips-queue-after-reboot.md.
/// </summary>
public interface IScriptCheckpointStore
{
    /// <summary>
    /// Returns the on-disk checkpoint for the given instance, or
    /// <see cref="ScriptCheckpoint.Empty"/> when no checkpoint has ever been
    /// written. Implementations MUST tolerate a missing / corrupt file by
    /// returning <c>Empty</c> rather than throwing — a damaged file should
    /// not block a fresh run.
    /// </summary>
    Task<ScriptCheckpoint> LoadAsync(string instanceId, CancellationToken cancellationToken);

    /// <summary>
    /// Persists the checkpoint. Implementations MUST write atomically
    /// (.tmp + rename) so a torn write cannot leave stale partial state.
    /// </summary>
    Task SaveAsync(string instanceId, ScriptCheckpoint checkpoint, CancellationToken cancellationToken);

    /// <summary>
    /// Wipes the checkpoint for the given instance. Called when the instance
    /// id changes (alongside per-instance semaphore reset).
    /// </summary>
    Task ResetAsync(string instanceId, CancellationToken cancellationToken);
}
