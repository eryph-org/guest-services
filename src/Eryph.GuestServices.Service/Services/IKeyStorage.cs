using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.DevTunnels.Ssh.Algorithms;

namespace Eryph.GuestServices.Service.Services;

public interface IKeyStorage
{
    public IKeyPair GetHostKey();

    public IKeyPair? GetClientKey();
}
