using System.Net.Sockets;

namespace Eryph.GuestServices.Client.ProxyTunnel;

// Applies the policy's keepalive to a data-plane socket egs-tool owns: SO_KEEPALIVE
// plus the per-socket idle / interval / retry. A quiet connection must survive, so
// probes start after idleSeconds and repeat every intervalSeconds up to retries
// before the socket is considered dead. The per-socket TCP options are best-effort:
// if a platform rejects one, SO_KEEPALIVE still stands and the SSH control-channel
// keepalive covers liveness, so a missing option degrades rather than fails.
internal static class SocketKeepAlive
{
    public static void Apply(Socket socket, KeepaliveSettings keepalive)
    {
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);

        TrySet(socket, SocketOptionName.TcpKeepAliveTime, keepalive.IdleSeconds);
        TrySet(socket, SocketOptionName.TcpKeepAliveInterval, keepalive.IntervalSeconds);
        TrySet(socket, SocketOptionName.TcpKeepAliveRetryCount, keepalive.Retries);
    }

    private static void TrySet(Socket socket, SocketOptionName option, int value)
    {
        try
        {
            socket.SetSocketOption(SocketOptionLevel.Tcp, option, value);
        }
        catch (SocketException)
        {
            // Platform does not support this per-socket TCP keepalive knob.
        }
        catch (PlatformNotSupportedException)
        {
            // Same: fall back to the plain SO_KEEPALIVE set above.
        }
    }
}
