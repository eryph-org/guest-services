using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Eryph.GuestServices.Sockets;

public static class PortNumberConverter
{
    private static readonly ReadOnlyMemory<byte> TemplateId = Guid.Parse("00000000-facb-11e6-bd58-64006a7986d3").ToByteArray().AsMemory();

    public static uint ToPortNumber(Guid serviceId)
    {
        var span = serviceId.ToByteArray().AsSpan();
        if (!TemplateId.Span[4..].SequenceEqual(span[4..]))
            throw new ArgumentException(
                $"The service ID {serviceId} does not match the expected template.",
                nameof(serviceId));

        return BitConverter.ToUInt32(span[..4]);
    }

    public static Guid ToIntegrationId(uint portNumber)
    {
        Span<byte> span = stackalloc byte[TemplateId.Length];
        TemplateId.Span.CopyTo(span);
        if (!BitConverter.TryWriteBytes(span[..4], portNumber))
            throw new ArgumentException(
                $"The port number {portNumber} cannot be converted to an integration ID.",
                nameof(portNumber));

        return new Guid(span);
    }
}
