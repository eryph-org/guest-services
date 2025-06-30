using Microsoft.DevTunnels.Ssh.Algorithms;

namespace Eryph.GuestServices.Service.Services;

public interface IHostKeyGenerator
{
    public IKeyPair GenerateHostKey();
}
