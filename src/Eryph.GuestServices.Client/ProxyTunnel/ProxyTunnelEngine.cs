using System.Net;
using System.Net.Sockets;
using Microsoft.DevTunnels.Ssh;
using Microsoft.DevTunnels.Ssh.Events;
using Microsoft.DevTunnels.Ssh.Tcp;

namespace Eryph.GuestServices.Client.ProxyTunnel;

// Runs a proxied tunnel in-process: opens the eryph/SSH channel through the
// supplied connector, and for every policy rule forwards bytes between a
// data-plane socket egs-tool owns and an SSH channel. No external forwarder and no
// listener beyond what the policy declares.
//
// egs-tool, not the DevTunnels library, owns the data plane:
//   * ingress: egs-tool opens its own host listener and relays each connection as a
//     direct-tcpip channel to the rule's single fixed target inside the guest.
//   * egress: egs-tool registers a guest-side loopback listener (tcpip-forward) and,
//     for each forwarded connection, dials the rule's single fixed host target with
//     its own socket.
// Because egs-tool owns both legs it sets TCP keepalive on each socket, and the SSH
// session carries a control-channel keepalive, so a quiet tunnel survives.
//
// Security pinning (the client must not be steered by a compromised guest):
//   * AcceptRemoteConnectionsForNonForwardedPorts = false — never accept a
//     guest-initiated direct-tcpip channel to an arbitrary host-local port.
//   * ForwardConnectionsToLocalPorts = false — the library never auto-dials; egress
//     targets are dialed here, only ever the rule's fixed 'to'.
//   * AcceptLocalConnectionsForForwardedPorts = false — no library-owned listener;
//     ingress listeners are opened here, loopback-bound unless the policy opts out.
public sealed class ProxyTunnelEngine(
    IGuestConnector connector,
    TunnelPolicy policy,
    Action<string>? writeInfo = null,
    Action<string>? writeWarning = null)
{
    private static readonly TimeSpan MaxReconnectBackoff = TimeSpan.FromSeconds(30);

    // Runs until cancelled. A rule that cannot be established fails closed
    // (TunnelPolicyException, never a fallback to an open forward); a dropped
    // session reconnects and re-establishes every rule.
    public async Task RunAsync(CancellationToken cancellation)
    {
        var everEstablished = false;
        var attempt = 0;

        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                await RunSessionAsync(() => everEstablished = true, cancellation);
                attempt = 0; // a clean session end resets the backoff
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                break;
            }
            catch (TunnelPolicyException)
            {
                // A rule was refused — fail closed, do not reconnect into a weaker setup.
                throw;
            }
            catch (Exception ex)
            {
                if (!everEstablished)
                    throw new TunnelPolicyException(
                        $"The tunnel '{policy.Tunnel}' could not be established: {ex.Message}");

                writeWarning?.Invoke(
                    $"Tunnel '{policy.Tunnel}' dropped: {ex.Message}. Reconnecting…");
            }

            if (cancellation.IsCancellationRequested)
                break;

            var backoff = TimeSpan.FromSeconds(Math.Min(MaxReconnectBackoff.TotalSeconds, Math.Pow(2, Math.Min(attempt, 5))));
            attempt++;
            await Task.Delay(backoff, cancellation);
        }
    }

    private async Task RunSessionAsync(Action markEstablished, CancellationToken cancellation)
    {
        await using var connection = await connector.ConnectAsync(cancellation);
        var session = connection.Session;

        var pfs = session.ActivateService<PortForwardingService>();
        pfs.AcceptRemoteConnectionsForNonForwardedPorts = false;
        pfs.ForwardConnectionsToLocalPorts = false;
        pfs.AcceptLocalConnectionsForForwardedPorts = false;

        using var sessionScope = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var dropped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnClosed(object? s, SshSessionClosedEventArgs e) => dropped.TrySetResult();
        void OnKeepAliveFailed(object? s, SshKeepAliveEventArgs e) => dropped.TrySetResult();
        session.Closed += OnClosed;
        session.KeepAliveFailed += OnKeepAliveFailed;

        var teardown = new List<IDisposable>();
        try
        {
            await EnsureGateAsync(pfs, cancellation);

            foreach (var rule in policy.Egress)
                teardown.Add(await EstablishEgressAsync(pfs, rule, sessionScope.Token, cancellation));

            foreach (var rule in policy.Ingress)
                teardown.Add(StartIngressListener(pfs, rule, sessionScope.Token));

            markEstablished();
            writeInfo?.Invoke(
                $"Tunnel '{policy.Tunnel}' is up: {policy.Egress.Count} egress, {policy.Ingress.Count} ingress. Press Ctrl+C to stop.");

            await using (cancellation.Register(() => dropped.TrySetResult()))
                await dropped.Task;
        }
        finally
        {
            session.Closed -= OnClosed;
            session.KeepAliveFailed -= OnKeepAliveFailed;
            sessionScope.Cancel();
            foreach (var disposable in teardown)
            {
                try { disposable.Dispose(); }
                catch { /* best-effort teardown */ }
            }
        }

        cancellation.ThrowIfCancellationRequested();
    }

    // Fail-closed gate probe. A throwaway reverse-forward on an ephemeral guest
    // loopback port exercises the exact tcpip-forward authorization an egress rule
    // needs; when PortForwardingEnabled is off the guest refuses it (null) and the
    // whole tunnel is refused rather than brought up partially. direct-tcpip
    // (ingress) is gated by the same server-side switch, so this one probe covers
    // both directions.
    private async Task EnsureGateAsync(PortForwardingService pfs, CancellationToken cancellation)
    {
        RemotePortStreamer? probe = await pfs.StreamFromRemotePortAsync(IPAddress.Loopback, 0, cancellation);
        if (probe is null)
            throw new TunnelPolicyException(
                $"The tunnel '{policy.Tunnel}' was refused by the guest: SSH port forwarding is not enabled "
                + "(set PortForwardingEnabled on the guest's egs-service to turn it on).");

        probe.Dispose();
    }

    private async Task<IDisposable> EstablishEgressAsync(
        PortForwardingService pfs, EgressRule rule, CancellationToken sessionToken, CancellationToken cancellation)
    {
        var streamer = await pfs.StreamFromRemotePortAsync(IPAddress.Loopback, rule.ListenPort, cancellation);
        if (streamer is null)
            throw new TunnelPolicyException(
                $"Egress rule '{rule.Name}': the guest refused to listen on loopback port {rule.ListenPort} "
                + "(port forwarding is off, or the port is already in use).");

        streamer.StreamOpened += (_, stream) => _ = RelayEgressAsync(rule, stream, sessionToken);
        writeInfo?.Invoke(
            $"egress '{rule.Name}': guest 127.0.0.1:{rule.ListenPort} -> {rule.ToHost}:{rule.ToPort}");
        return streamer;
    }

    private async Task RelayEgressAsync(EgressRule rule, Stream guestStream, CancellationToken cancellation)
    {
        try
        {
            using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(rule.ToHost, rule.ToPort, cancellation);
            SocketKeepAlive.Apply(socket, policy.Keepalive);

            await using var hostStream = new NetworkStream(socket, ownsSocket: false);
            await BridgeAsync(guestStream, hostStream, cancellation);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            writeWarning?.Invoke(
                $"egress '{rule.Name}': connection to {rule.ToHost}:{rule.ToPort} failed: {ex.Message}");
        }
        finally
        {
            await guestStream.DisposeAsync();
        }
    }

    private IDisposable StartIngressListener(PortForwardingService pfs, IngressRule rule, CancellationToken sessionToken)
    {
        var listener = new TcpListener(rule.Listen);
        listener.Start();
        writeInfo?.Invoke(
            $"ingress '{rule.Name}': host {rule.Listen} -> guest {rule.ToHost}:{rule.ToPort}");

        _ = AcceptIngressLoopAsync(pfs, rule, listener, sessionToken);
        return new ListenerHandle(listener);
    }

    private async Task AcceptIngressLoopAsync(
        PortForwardingService pfs, IngressRule rule, TcpListener listener, CancellationToken cancellation)
    {
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                var client = await listener.AcceptSocketAsync(cancellation);
                _ = RelayIngressAsync(pfs, rule, client, cancellation);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (SocketException) { }
    }

    private async Task RelayIngressAsync(
        PortForwardingService pfs, IngressRule rule, Socket client, CancellationToken cancellation)
    {
        try
        {
            SocketKeepAlive.Apply(client, policy.Keepalive);

            var guestStream = await pfs.StreamToRemotePortAsync(rule.ToHost, rule.ToPort, cancellation);
            if (guestStream is null)
                throw new IOException("the guest refused the connection (port forwarding is off).");

            using (client)
            await using (var hostStream = new NetworkStream(client, ownsSocket: false))
            await using (guestStream)
                await BridgeAsync(hostStream, guestStream, cancellation);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            writeWarning?.Invoke($"ingress '{rule.Name}': {ex.Message}");
            try { client.Dispose(); }
            catch { /* already disposed by the using above */ }
        }
    }

    // Bidirectional copy that ends when either side ends; disposing the streams
    // (done by each relay's using/await using) unblocks the still-running direction.
    private static async Task BridgeAsync(Stream left, Stream right, CancellationToken cancellation)
    {
        var toRight = CopyAsync(left, right, cancellation);
        var toLeft = CopyAsync(right, left, cancellation);
        await Task.WhenAny(toRight, toLeft);
    }

    private static async Task CopyAsync(Stream from, Stream to, CancellationToken cancellation)
    {
        try
        {
            await from.CopyToAsync(to, cancellation);
        }
        catch
        {
            // Peer closed, cancelled, or the paired direction disposed the streams.
            // The relay's teardown propagates the close to the other leg.
        }
    }

    private sealed class ListenerHandle(TcpListener listener) : IDisposable
    {
        public void Dispose() => listener.Stop();
    }
}
