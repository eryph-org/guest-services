using System.Net.Sockets;
using Eryph.GuestServices.Core;
using Eryph.GuestServices.Sockets;
using Microsoft.DevTunnels.Ssh.Algorithms;

namespace Eryph.GuestServices.Client;

// Reaches the guest over the local Hyper-V socket. This is egs-tool's original
// transport; it requires host access and trusts the server because the socket is
// only reachable by local administrators. The caller supplies the key pair to
// authenticate with (the machine-wide service key for egs-tool, or any key the
// guest authorizes for an embedding host).
public sealed class HyperVGuestConnector(Guid vmId, IKeyPair keyPair) : IGuestConnector
{
    public async Task<GuestSshConnection> ConnectAsync(CancellationToken cancellation)
    {
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
