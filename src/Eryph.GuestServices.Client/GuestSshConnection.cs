using Microsoft.DevTunnels.Ssh;

namespace Eryph.GuestServices.Client;

// An authenticated guest SSH session bundled with the transport stream it runs
// on, so a command can 'await using' a single object and dispose both in the
// right order regardless of which transport produced them.
public sealed class GuestSshConnection(SshClientSession session, Stream transport) : IAsyncDisposable
{
    public SshClientSession Session { get; } = session;

    public async ValueTask DisposeAsync()
    {
        // Close the SSH session before the underlying stream: disposing the
        // transport first would fault the session's final teardown messages. The
        // finally guarantees the transport (and, for the eryph channel, the
        // WebSocket it owns) is still released even if the session dispose throws.
        try
        {
            Session.Dispose();
        }
        finally
        {
            await transport.DisposeAsync();
        }
    }
}
