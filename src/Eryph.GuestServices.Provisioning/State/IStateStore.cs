namespace Eryph.GuestServices.Provisioning.State;

public interface IStateStore
{
    Task<ProvisioningState?> LoadAsync(CancellationToken cancellationToken);

    Task SaveAsync(ProvisioningState state, CancellationToken cancellationToken);

    Task ResetAsync(CancellationToken cancellationToken);
}
