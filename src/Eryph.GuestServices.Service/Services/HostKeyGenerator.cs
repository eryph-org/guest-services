using Microsoft.DevTunnels.Ssh;
using Microsoft.DevTunnels.Ssh.Algorithms;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Eryph.GuestServices.Service.Services;

public class HostKeyGenerator : IHostKeyGenerator
{
    public IKeyPair GenerateHostKey()
    {
        return SshAlgorithms.PublicKey.ECDsaSha2Nistp256.GenerateKeyPair();
    }
}
