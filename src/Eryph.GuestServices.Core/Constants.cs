using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Eryph.GuestServices.Core;

public static class Constants
{
    /// <summary>
    /// The Hyper-V integration service ID for the eryph guest services.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This ID must be registered on the VM host before the eryph guest services can be used.
    /// </para>
    /// <para>
    /// This ID corresponds to the port 5002 for Linux VSock sockets.
    /// </para>
    /// </remarks>
    public static readonly Guid ServiceId = Guid.Parse("0000138a-facb-11e6-bd58-64006a7986d3");

    public static readonly string ServiceName = "Eryph Guest Services";
}
