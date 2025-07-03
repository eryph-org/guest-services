using System.Net;
using System.Net.Sockets;

namespace Eryph.GuestServices.Sockets;

public class HyperVEndPoint : EndPoint
{
    private const AddressFamily HyperVAddressFamily = (AddressFamily)34;
    private const int AddressLength = 36;

    public HyperVEndPoint(Guid vmId, Guid serviceId)
    {
        VmId = vmId;
        ServiceId = serviceId;
    }

    public HyperVEndPoint(Guid vmId, uint portNumber) : this(vmId, PortNumberConverter.ToIntegrationId(portNumber))
    {
    }

    public override AddressFamily AddressFamily => HyperVAddressFamily;

    public Guid VmId { get; }

    public Guid ServiceId { get; }

    // The address layout is defined by SOCKADDR_HV

    public override EndPoint Create(SocketAddress socketAddress)
    {
        if (socketAddress.Family != HyperVAddressFamily || socketAddress.Size != AddressLength)
            throw new ArgumentException("Invalid HyperV socket address.");

        var vmId = new Guid(socketAddress.Buffer.Span[4..20]);
        var integrationId = new Guid(socketAddress.Buffer.Span[20..36]);

        return new HyperVEndPoint(vmId, integrationId);
    }

    public override SocketAddress Serialize()
    {
        var socketAddress = new SocketAddress(AddressFamily, AddressLength);
        var span = socketAddress.Buffer.Span;

        VmId.TryWriteBytes(span[4..]);
        ServiceId.TryWriteBytes(span[20..]);
        
        return socketAddress;
    }
}
