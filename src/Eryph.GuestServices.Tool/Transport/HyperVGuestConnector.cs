using System.Net.Sockets;
using Eryph.GuestServices.Core;
using Eryph.GuestServices.Sockets;

namespace Eryph.GuestServices.Tool.Transport;

// Reaches the guest over the local Hyper-V socket using the machine-wide service
// client key created by 'initialize'. This is egs-tool's original transport; it
// requires host access and the elevated key, and trusts the server because the
// socket is only reachable by local administrators.
internal sealed class HyperVGuestConnector(Guid vmId) : IGuestConnector
{
    public async Task<GuestSshConnection> ConnectAsync(CancellationToken cancellation)
    {
        var keyPair = await ClientKeyHelper.GetKeyPairAsync()
            ?? throw new GuestConnectionException("No SSH key found. Have you run the initialize command?");

        var socket = await SocketFactory.CreateClientSocket(vmId, Constants.ServiceId);
        var stream = new NetworkStream(socket, ownsSocket: true);
        try
        {
            var session = await GuestSsh.EstablishSessionAsync(stream, keyPair);
            return new GuestSshConnection(session, stream);
        }
        catch
        {
            await stream.DisposeAsync();
            throw;
        }
    }
}
