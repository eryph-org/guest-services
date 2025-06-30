using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Eryph.GuestServices.Sockets;

public enum ListenMode
{
    Any,
    Parent,
    Children,
    Loopback,
}
