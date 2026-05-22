namespace Eryph.GuestServices.Provisioning.Semaphores;

/// <summary>
/// Detects whether the current process is running in a different boot
/// session than the last time the provisioning agent ran. Used to clear
/// <see cref="Stages.ModuleFrequency.PerBoot"/> semaphores at the start
/// of each boot.
/// </summary>
public interface IBootSessionDetector
{
    /// <summary>
    /// Returns <c>true</c> the first time the agent observes a particular
    /// boot session; subsequent calls within the same boot return
    /// <c>false</c>. The implementation persists the last-observed boot
    /// marker so the detection survives agent restarts within a single boot.
    /// </summary>
    Task<bool> IsNewBootAsync(CancellationToken cancellationToken);
}
