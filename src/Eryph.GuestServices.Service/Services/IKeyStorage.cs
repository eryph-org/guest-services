using Microsoft.DevTunnels.Ssh.Algorithms;

namespace Eryph.GuestServices.Service.Services;

public interface IKeyStorage
{
    public Task<IKeyPair?> GetClientKeyAsync();

    public Task SetClientKeyAsync(IKeyPair keyPair);

    public Task<IKeyPair?> GetHostKeyAsync();

    public Task SetHostKeyAsync(IKeyPair keyPair);
}
