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
/// <para>
/// The requested bind <em>interface</em> is deliberately overridden to loopback —
/// that is the whole point. The requested port, and the requested address
/// <em>family</em>, are honored.
/// </para>
/// <para>
/// Honoring the family matters: for a loopback/any request the library's
/// <c>LocalPortForwarder.StartForwardingAsync</c> calls this factory twice — once
/// for IPv4, then again for IPv6 on the <b>same</b> port — and only tolerates
/// <c>AddressNotAvailable</c> on the second call. Returning an IPv4 loopback for
/// both would make the second bind fail with <c>AddressAlreadyInUse</c> and the
/// whole reverse forward be refused. Binding the loopback of the requested family
/// (<c>127.0.0.1</c> then <c>::1</c>) lets both calls succeed, loopback-only.
/// </para>
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
        var loopback = localIPAddress.AddressFamily == AddressFamily.InterNetworkV6
            ? IPAddress.IPv6Loopback
            : IPAddress.Loopback;
        var listener = new TcpListener(loopback, localPort);
        listener.Start();
        return Task.FromResult(listener);
    }
}
