using Microsoft.DevTunnels.Ssh;
using Microsoft.DevTunnels.Ssh.Algorithms;

namespace Eryph.GuestServices.Service.Services;

public class HostKeyGenerator : IHostKeyGenerator
{
    public IKeyPair GenerateHostKey()
    {
        return SshAlgorithms.PublicKey.ECDsaSha2Nistp256.GenerateKeyPair();
    }
}
