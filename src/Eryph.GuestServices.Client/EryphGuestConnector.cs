using System.Net.WebSockets;
using Microsoft.DevTunnels.Ssh;
using Microsoft.DevTunnels.Ssh.Algorithms;

namespace Eryph.GuestServices.Client;

// Reaches the guest over the eryph remote channel: opens the data-plane WebSocket
// (EryphChannel) and runs the SSH session over it in-process via WebSocketStream.
//
// The caller supplies the resolved connection and the key pair to authenticate
// with. The guest must already authorize that public key, either pre-injected at
// build time or pushed with GuestAccessKey.AddAsync ('catlet add-key').
public sealed class EryphGuestConnector(
    EryphConnection connection,
    string catletId,
    IKeyPair keyPair,
    Action<string>? writeWarning = null) : IGuestConnector
{
    public async Task<GuestSshConnection> ConnectAsync(CancellationToken cancellation)
    {
        var webSocket = await EryphChannel.OpenAsync(connection, catletId, writeWarning, cancellation);

        // Run SSH directly over the channel in-process. The .NET 10 WebSocketStream
        // wraps the socket as a byte stream (binary frames, 0-byte read on close)
        // and owns it, so disposing the stream closes the socket. Until the stream
        // exists the socket is disposed directly on failure so it never leaks.
        Stream stream;
        try
        {
            stream = WebSocketStream.Create(webSocket, WebSocketMessageType.Binary, ownsWebSocket: true);
        }
        catch
        {
            webSocket.Dispose();
            throw;
        }

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
