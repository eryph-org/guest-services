using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Eryph.GuestServices.Sockets;

public static class HyperVAddresses
{
    public static readonly Guid Wildcard = Guid.Parse("00000000-0000-0000-0000-000000000000");

    public static readonly Guid Children = Guid.Parse("90db8b89-0d35-4f79-8ce9-49ea0ac8b7cd");

    public static readonly Guid Parent = Guid.Parse("a42e7cda-d03f-480c-9cc2-a4de20abb878");

    public static readonly Guid Loopback = Guid.Parse("e0e16197-dd56-4a10-9195-5ee7a155a838");
}
