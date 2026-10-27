using System.Net;
using AwesomeAssertions;
using Microsoft.DevTunnels.Ssh.Events;
using Microsoft.DevTunnels.Ssh.Tcp;

namespace Eryph.GuestServices.DevTunnels.Ssh.Extensions.Tests;

/// <summary>
/// End-to-end (in-process, real client + server over a loopback socket) proof of
/// the reverse-forward (<c>ssh -R</c>) <b>authorization</b> contract that
/// <see cref="SocketSshServer.SessionRequest"/> exists to serve: a
/// <c>tcpip-forward</c> session request is honored only when a subscriber sets
/// <see cref="SshRequestEventArgs{T}.IsAuthorized"/>. This pins the new event
/// wiring and our assumption about the DevTunnels library's dispatch — a unit
/// test over the pure decision helper cannot, because deleting the subscription
/// leaves it green. It does not cover the loopback bind posture (that is the
/// production handler's job, pinned by the SshServerService / LoopbackTcpListenerFactory
/// unit tests); these tests use the default factory and so only assert
/// authorized-vs-refused.
/// </summary>
[Collection("e2e")]
public class SessionRequestForwardingTests
{
    [Fact]
    public async Task ReverseForward_IsAccepted_WhenSessionRequestIsAuthorized()
    {
        using var helper = new SshTestHelper();
        await helper.SetupAsync(typeof(PortForwardingService));

        // The production server (SshServerService) authorizes tcpip-forward here;
        // the test stands in for that subscriber.
        helper.Server.SessionRequest += (_, e) =>
        {
            if (e.RequestType is "tcpip-forward" or "cancel-tcpip-forward")
                e.IsAuthorized = true;
        };

        var client = helper.ClientSession.ActivateService<PortForwardingService>();
        var remotePort = GetFreeLoopbackPort();

        var forwarder = await client.ForwardFromRemotePortAsync(
            IPAddress.Loopback, remotePort, "localhost", GetFreeLoopbackPort(), CancellationToken.None);

        try
        {
            forwarder.Should().NotBeNull("the server authorized the tcpip-forward request");
        }
        finally
        {
            forwarder?.Dispose();
        }
    }

    [Fact]
    public async Task ReverseForward_IsRefused_WhenNobodyAuthorizesTheSessionRequest()
    {
        using var helper = new SshTestHelper();
        await helper.SetupAsync(typeof(PortForwardingService));

        // No SessionRequest subscriber: IsAuthorized stays false, so the library
        // must refuse the request (the default-deny this whole change relies on).
        var client = helper.ClientSession.ActivateService<PortForwardingService>();
        var remotePort = GetFreeLoopbackPort();

        var forwarder = await client.ForwardFromRemotePortAsync(
            IPAddress.Loopback, remotePort, "localhost", GetFreeLoopbackPort(), CancellationToken.None);

        forwarder.Should().BeNull("an unauthorized tcpip-forward request must be refused");
    }

    private static int GetFreeLoopbackPort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }
}
