namespace Eryph.GuestServices.Provisioning.State;

/// <summary>
/// In-memory state store used by <c>--dry-run</c>. Keeps the current
/// <see cref="ProvisioningState"/> alive for the duration of the run so
/// the stage runner can observe its own progress, but never touches disk.
/// </summary>
internal sealed class NullStateStore : IStateStore
{
    private ProvisioningState? _state;

    public Task<ProvisioningState?> LoadAsync(CancellationToken cancellationToken) =>
        Task.FromResult(_state);

    public Task SaveAsync(ProvisioningState state, CancellationToken cancellationToken)
    {
        _state = state;
        return Task.CompletedTask;
    }

    public Task ResetAsync(CancellationToken cancellationToken)
    {
        _state = null;
        return Task.CompletedTask;
    }
}
