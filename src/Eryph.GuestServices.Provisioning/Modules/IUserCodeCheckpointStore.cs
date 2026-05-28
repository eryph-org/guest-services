namespace Eryph.GuestServices.Provisioning.Modules;

/// <summary>
/// Persists per-instance execution state shared by <see cref="RuncmdModule"/>
/// and <see cref="ScriptsUserModule"/>. Concrete subtypes pin the on-disk
/// filename so each module gets its own slot under
/// <c>%ProgramData%\eryph\provisioning\instance\&lt;id&gt;\</c>.
/// </summary>
public interface IUserCodeCheckpointStore
{
    /// <summary>
    /// Returns the on-disk checkpoint for the given instance, or
    /// <see cref="UserCodeCheckpoint.Empty"/> when no checkpoint has ever been
    /// written. Implementations MUST tolerate a missing / corrupt file by
    /// returning <c>Empty</c> rather than throwing.
    /// </summary>
    Task<UserCodeCheckpoint> LoadAsync(string instanceId, CancellationToken cancellationToken);

    /// <summary>
    /// Persists the checkpoint atomically (.tmp + rename) so a torn write
    /// cannot leave stale partial state.
    /// </summary>
    Task SaveAsync(string instanceId, UserCodeCheckpoint checkpoint, CancellationToken cancellationToken);

    /// <summary>
    /// Wipes the checkpoint for the given instance.
    /// </summary>
    Task ResetAsync(string instanceId, CancellationToken cancellationToken);
}

/// <summary>Marker for the runcmd module's checkpoint slot.</summary>
public interface IRuncmdCheckpointStore : IUserCodeCheckpointStore { }

/// <summary>Marker for the scripts module's checkpoint slot.</summary>
public interface IScriptCheckpointStore : IUserCodeCheckpointStore { }
