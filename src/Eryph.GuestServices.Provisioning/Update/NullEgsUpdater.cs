using Eryph.GuestServices.CloudConfig;

namespace Eryph.GuestServices.Provisioning.Update;

/// <summary>
/// No-op updater used in dry-run mode: never downloads or stages anything, so a
/// what-if run has no network or disk side effects.
/// </summary>
public sealed class NullEgsUpdater : IEgsUpdater
{
    public Task<UpdatePlan?> PrepareAsync(EgsUpdateConfig? config, CancellationToken cancellationToken) =>
        Task.FromResult<UpdatePlan?>(null);
}
