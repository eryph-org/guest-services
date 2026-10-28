using System.Net;
using System.Net.Sockets;
using System.Text;
using AwesomeAssertions;
using Eryph.GuestServices.Client;
using Eryph.GuestServices.Client.ProxyTunnel;
using Microsoft.DevTunnels.Ssh;
using Microsoft.DevTunnels.Ssh.Tcp;

namespace Eryph.GuestServices.DevTunnels.Ssh.Extensions.Tests;

/// <summary>
/// In-process (real client + server over a loopback socket) proof of the proxied
/// tunnel engine's data path and its fail-closed gate. A unit test over the policy
/// loader cannot cover these — they only hold against a live SSH session and the
/// real PortForwardingService.
/// </summary>
[Collection("e2e")]
public class ProxyTunnelEngineTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static readonly KeepaliveSettings Keepalive = new(IdleSeconds: 1, IntervalSeconds: 5, Retries: 3);

    [Fact]
    public async Task Egress_relays_a_guest_connection_to_the_single_host_target()
    {
        using var hardTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var helper = new SshTestHelper();
        await helper.SetupAsync(typeof(PortForwardingService));
        AuthorizeReverseForwards(helper);

        // The host-side target egs-tool is allowed to dial: on connect it sends a
        // token and half-closes, so the reader sees the token then EOF.
        var token = $"egs-egress-{Guid.NewGuid():N}";
        var guestPort = GetFreeLoopbackPort();
        var toPort = GetFreeLoopbackPort();
        using var target = StartTokenServer(toPort, token, hardTimeout.Token);

        var policy = new TunnelPolicy(
            "test", "catlet",
            Egress: [new EgressRule("e", guestPort, "127.0.0.1", toPort)],
            Ingress: [],
            Keepalive);

        using var run = CancellationTokenSource.CreateLinkedTokenSource(hardTimeout.Token);
        var engine = new ProxyTunnelEngine(
            new FixedConnector(helper.ClientSession!), policy,
            writeInfo: m => output.WriteLine($"info: {m}"),
            writeWarning: m => output.WriteLine($"warn: {m}"));
        var engineTask = engine.RunAsync(run.Token);

        // A "guest process" connects to the guest-side loopback listener the server
        // opened for the reverse forward; the engine dials the host target and
        // bridges, so the token flows back here. Poll until the forward is live.
        var received = await ReadTokenWithRetryAsync(guestPort, hardTimeout.Token);

        run.Cancel();
        await engineTask;

        received.Should().Be(token);
    }

    [Fact]
    public async Task Guest_cannot_open_a_channel_back_to_a_host_port()
    {
        // The whole VM (incl. egs-service) is untrusted: once the tunnel is up, the
        // guest must not be able to reach back through egs-tool to an arbitrary
        // host-local port. The engine pins AcceptRemoteConnectionsForNonForwardedPorts
        // = false on the client, so a guest-initiated direct-tcpip must be refused.
        using var hardTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var helper = new SshTestHelper();
        await helper.SetupAsync(typeof(PortForwardingService));
        AuthorizeReverseForwards(helper);

        var forbiddenPort = GetFreeLoopbackPort();
        using var sentinel = StartTokenServer(forbiddenPort, "SHOULD-NOT-BE-REACHED", hardTimeout.Token);

        var up = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var policy = new TunnelPolicy(
            "test", "catlet",
            Egress: [new EgressRule("e", GetFreeLoopbackPort(), "127.0.0.1", GetFreeLoopbackPort())],
            Ingress: [],
            Keepalive);

        using var run = CancellationTokenSource.CreateLinkedTokenSource(hardTimeout.Token);
        var engine = new ProxyTunnelEngine(
            new FixedConnector(helper.ClientSession!), policy,
            writeInfo: m => { if (m.Contains("is up")) up.TrySetResult(); });
        var engineTask = engine.RunAsync(run.Token);
        await up.Task.WaitAsync(hardTimeout.Token);

        // The guest (server) attempts a direct-tcpip channel back to the host port.
        var guestPfs = helper.ServerSession!.ActivateService<PortForwardingService>();
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(hardTimeout.Token);
        attempt.CancelAfter(TimeSpan.FromSeconds(8));
        SshStream? back = null;
        try
        {
            back = await guestPfs.StreamToRemotePortAsync("127.0.0.1", forbiddenPort, attempt.Token);
        }
        catch
        {
            // A refusal surfaced as an exception is equally acceptable.
        }

        run.Cancel();
        await engineTask;

        back.Should().BeNull(
            "the client pins AcceptRemoteConnectionsForNonForwardedPorts=false, so a guest-initiated "
            + "direct-tcpip to a host port must be refused");
    }

    [Fact]
    public async Task Gate_probe_fails_closed_when_the_guest_refuses_forwarding()
    {
        using var hardTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var helper = new SshTestHelper();
        // No SessionRequest authorizer: tcpip-forward is refused, exactly like a
        // guest with PortForwardingEnabled off.
        await helper.SetupAsync(typeof(PortForwardingService));

        var policy = new TunnelPolicy(
            "test", "catlet",
            Egress: [new EgressRule("e", GetFreeLoopbackPort(), "127.0.0.1", GetFreeLoopbackPort())],
            Ingress: [],
            Keepalive);

        var engine = new ProxyTunnelEngine(new FixedConnector(helper.ClientSession!), policy);

        var act = () => engine.RunAsync(hardTimeout.Token);

        await act.Should().ThrowAsync<TunnelPolicyException>()
            .Where(ex => ex.Message.Contains("port forwarding", StringComparison.OrdinalIgnoreCase));
    }

    private static void AuthorizeReverseForwards(SshTestHelper helper) =>
        helper.Server!.SessionRequest += (_, e) =>
        {
            if (e.RequestType is "tcpip-forward" or "cancel-tcpip-forward")
                e.IsAuthorized = true;
        };

    private static async Task<string> ReadTokenWithRetryAsync(int guestPort, CancellationToken cancellation)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(15);
        Exception? last = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, guestPort, cancellation);
                await using var stream = client.GetStream();
                var buffer = new byte[256];
                var read = await stream.ReadAsync(buffer, cancellation);
                if (read > 0)
                    return Encoding.ASCII.GetString(buffer, 0, read);
            }
            catch (Exception ex) when (ex is SocketException or IOException)
            {
                last = ex;
            }

            await Task.Delay(200, cancellation);
        }

        throw new TimeoutException($"The egress forward never carried the token. Last error: {last?.Message}");
    }

    private static TokenServer StartTokenServer(int port, string token, CancellationToken cancellation)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        var bytes = Encoding.ASCII.GetBytes(token);
        var loop = Task.Run(async () =>
        {
            try
            {
                while (!cancellation.IsCancellationRequested)
                {
                    var socket = await listener.AcceptSocketAsync(cancellation);
                    await socket.SendAsync(bytes, SocketFlags.None, cancellation);
                    socket.Shutdown(SocketShutdown.Send);
                    socket.Dispose();
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (SocketException) { }
        }, cancellation);

        return new TokenServer(listener, loop);
    }

    private static int GetFreeLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
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

    private sealed class FixedConnector(SshClientSession session) : IGuestConnector
    {
        // The engine owns no transport of its own in this test; the session is the
        // helper's and is disposed by the helper, so hand the connection a throwaway
        // stream to satisfy GuestSshConnection's dispose contract.
        public Task<GuestSshConnection> ConnectAsync(CancellationToken cancellation) =>
            Task.FromResult(new GuestSshConnection(session, new MemoryStream()));
    }

    private sealed class TokenServer(TcpListener listener, Task loop) : IDisposable
    {
        public void Dispose()
        {
            listener.Stop();
            try { loop.Wait(TimeSpan.FromSeconds(2)); }
            catch { /* best effort */ }
        }
    }
}
