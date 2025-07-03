using Microsoft.DevTunnels.Ssh.Algorithms;

namespace Eryph.GuestServices.Service.Services;

public interface IKeyStorage
{
    public IKeyPair GetHostKey();

    public IKeyPair? GetClientKey();
}
