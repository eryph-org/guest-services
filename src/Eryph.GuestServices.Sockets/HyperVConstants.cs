using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace Eryph.GuestServices.Sockets;

public static class HyperVConstants
{
    public static readonly AddressFamily AddressFamily = (AddressFamily)34;

    public static readonly ProtocolType ProtocolType = (ProtocolType)1;

    public static readonly Guid HyperVParentId = new("a42e7cda-d03f-480c-9cc2-a4de20abb878");
}
