using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.DevTunnels.Ssh.Tcp;

namespace Eryph.GuestServices.Service.Services;

/// <summary>
/// An <see cref="ITcpListenerFactory"/> that pins every reverse-forward
/// (<c>ssh -R</c>) listener to the loopback interface, regardless of the bind
/// address the client requested. This enforces OpenSSH's default
/// <c>GatewayPorts no</c> posture: a bare <c>ssh -R 8080:…</c> sends an empty
/// bind address, which the DevTunnels library maps to <see cref="IPAddress.Any"/>
/// (all interfaces) and would expose the guest on its network. Installed on the
/// per-session <c>PortForwardingService</c> only while <c>GatewayPorts</c> is
/// off; when the operator opts in, the library's default factory honors the
/// requested bind address instead.
/// </summary>
/// <remarks>
/// The requested bind address is deliberately ignored — that is the whole point.
/// The requested port is honored. Mirrors the library's
/// <c>DefaultTcpListenerFactory</c> otherwise (create, start, return).
/// </remarks>
internal sealed class LoopbackTcpListenerFactory : ITcpListenerFactory
{
    public static readonly LoopbackTcpListenerFactory Instance = new();

    public Task<TcpListener> CreateTcpListenerAsync(
        int? remotePort,
        IPAddress localIPAddress,
        int localPort,
        bool canChangeLocalPort,
        TraceSource trace,
        CancellationToken cancellation)
    {
        var listener = new TcpListener(IPAddress.Loopback, localPort);
        listener.Start();
        return Task.FromResult(listener);
    }
}
