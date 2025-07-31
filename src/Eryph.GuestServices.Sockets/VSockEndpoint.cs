using System.Net;
using System.Net.Sockets;

namespace Eryph.GuestServices.Sockets;

public class VSockEndpoint : EndPoint
{
    private const AddressFamily VSockAddressFamily = (AddressFamily)40;
    private const int AddressLength = 16;

    public VSockEndpoint(uint cid, uint portNumber)
    {
        Cid = cid;
        Port = portNumber;
    }

    public uint Cid { get; }

    public uint Port { get; }

    public override AddressFamily AddressFamily => VSockAddressFamily;

    public override EndPoint Create(SocketAddress socketAddress)
    {
        // We cannot really validate anything here:
        // socketAddress.Family == VsockAddressFamily will fail.
        if (socketAddress.Size != AddressLength)
            throw new ArgumentException("Invalid VSock socket address.");

        var cid = BitConverter.ToUInt32(socketAddress.Buffer.Span[4..8]);
        var port = BitConverter.ToUInt32(socketAddress.Buffer.Span[8..12]);
        return new VSockEndpoint(cid, port);
    }

    public override SocketAddress Serialize()
    {
        return new VSockSocketAddress(Cid, Port);
    }

    // On Linux, .NET validates the address family when the SocketAddress is created.
    // Unfortunately, the AF_VSOCK address family is not yet defined .NET.
    // Hence, we use a workaround which is documented in the .NET unit tests
    // and overwrite the address family in the constructor.
    // Otherwise, using an unknown (to .NET) socket type should be supported
    // as .NET explicitly covers that scenario with a unit test.
    // https://github.com/dotnet/runtime/blob/3c5f74af89e331a5474025ce56d146ee180e1887/src/libraries/System.Net.Sockets/tests/FunctionalTests/CreateSocketTests.cs#L601
    private class VSockSocketAddress : SocketAddress
    {
        public VSockSocketAddress(uint cid, uint port) : base(AddressFamily.Packet, AddressLength)
        {
            var span = Buffer.Span;
            BitConverter.TryWriteBytes(span[..2], (ushort)VSockAddressFamily);
            BitConverter.TryWriteBytes(span[4..8], port);
            BitConverter.TryWriteBytes(span[8..12], cid);
        }
    }
}
