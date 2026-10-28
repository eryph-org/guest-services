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

    // Cap on concurrent data-plane connections per rule. For egress this bounds a
    // malicious guest's ability to amplify connections through the host against the
    // fixed target; it also bounds how many half-closed relays can accumulate.
    private const int MaxConnectionsPerRule = 128;

    // How long a session must stay up before we treat the next drop as a fresh
    // flap and reset the backoff — so an immediately-closing guest backs off instead
    // of hot-looping at the minimum delay.
    private static readonly TimeSpan StableThreshold = TimeSpan.FromSeconds(30);

    // Transient failures before the tunnel has ever come up are retried this many
    // times (a slow eryph channel, say) before we give up and report startup failure.
    private const int StartupRetryLimit = 5;

    // Runs until cancelled. A rule that cannot be established because the guest
    // refuses port forwarding fails closed (TunnelPolicyException, never a fallback
    // to an open forward); a dropped session or a transient error reconnects and
    // re-establishes every rule.
    public async Task RunAsync(CancellationToken cancellation)
    {
        var everEstablished = false;
        var attempt = 0;

        while (!cancellation.IsCancellationRequested)
        {
            var sessionStart = DateTimeOffset.UtcNow;
            try
            {
                await RunSessionAsync(
                    () =>
                    {
                        everEstablished = true;
                        sessionStart = DateTimeOffset.UtcNow;
                    },
                    cancellation);

                // A normal return means the session dropped (cancellation would have
                // thrown). Fall through to the reconnect backoff.
                writeWarning?.Invoke($"Tunnel '{policy.Tunnel}' dropped. Reconnecting…");
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                break;
            }
            catch (TunnelPolicyException)
            {
                // The guest refused a rule (port forwarding off): fail closed, never
                // reconnect into a weaker setup.
                throw;
            }
            catch (Exception ex)
            {
                if (!everEstablished && attempt >= StartupRetryLimit)
                    throw new TunnelPolicyException(
                        $"The tunnel '{policy.Tunnel}' could not be established: {ex.Message}");

                writeWarning?.Invoke(
                    everEstablished
                        ? $"Tunnel '{policy.Tunnel}' dropped: {ex.Message}. Reconnecting…"
                        : $"Tunnel '{policy.Tunnel}' not up yet: {ex.Message}. Retrying…");
            }

            if (cancellation.IsCancellationRequested)
                break;

            // Reset the backoff only after a session that actually stayed up.
            if (everEstablished && DateTimeOffset.UtcNow - sessionStart > StableThreshold)
                attempt = 0;

            var backoff = TimeSpan.FromSeconds(
                Math.Min(MaxReconnectBackoff.TotalSeconds, Math.Pow(2, Math.Min(attempt, 5))));
            attempt++;

            try
            {
                await Task.Delay(backoff, cancellation);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task RunSessionAsync(Action markEstablished, CancellationToken cancellation)
    {
        await using var connection = await connector.ConnectAsync(cancellation);
        var session = connection.Session;

        var pfs = session.ActivateService<PortForwardingService>();
        // The whole VM (incl. the guest SSH server) is untrusted, so pin the client's
        // forwarding posture. GuestSsh already set these before connect for the eryph
        // path; re-assert them here (idempotent) so the engine is correct for any
        // connector and the invariant lives next to the code that depends on it.
        //
        //   AcceptRemoteConnectionsForNonForwardedPorts = false is THE control that
        //   stops a guest-initiated direct-tcpip from reaching a host-local port. Note
        //   ForwardConnectionsToLocalPorts stays at its default (true): the library
        //   WOULD dial a guest-supplied host:port for such a channel, and the only
        //   thing preventing that is this flag being false — do not relax it. (It must
        //   also stay true because the RemotePortStreamer that delivers egress
        //   connections rides that path; false silently drops every egress connection.)
        pfs.AcceptRemoteConnectionsForNonForwardedPorts = false;
        // No library-owned local listener; ingress listeners are opened here,
        // loopback-bound unless the policy opts out.
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
    // both directions and lets a later per-rule refusal be read as "port in use"
    // rather than "forwarding off".
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
            // The gate probe already proved forwarding is on, so a null here is a
            // busy/stale guest listener on this port — transient, let the reconnect
            // path retry rather than failing the whole tunnel.
            throw new IOException(
                $"egress rule '{rule.Name}': the guest loopback port {rule.ListenPort} is not available (in use).");

        // Do not trust the port the guest echoes back: if it does not match what we
        // asked to forward, refuse rather than carry a rule whose audit line would lie.
        if (streamer.RemotePort != rule.ListenPort)
        {
            streamer.Dispose();
            throw new TunnelPolicyException(
                $"egress rule '{rule.Name}': the guest bound port {streamer.RemotePort}, not the requested {rule.ListenPort}.");
        }

        var limiter = new ConnectionLimiter(MaxConnectionsPerRule);
        streamer.StreamOpened += (_, stream) =>
        {
            // A guest can open connections on its loopback listener at will; cap how
            // many we will carry through the host at once for this rule.
            if (!limiter.TryAcquire())
            {
                writeWarning?.Invoke(
                    $"egress '{rule.Name}': refused a connection, {MaxConnectionsPerRule} concurrent limit reached.");
                _ = stream.DisposeAsync();
                return;
            }

            StartEgressRelay(rule, stream, limiter, sessionToken);
        };
        writeInfo?.Invoke(
            $"egress '{rule.Name}': guest 127.0.0.1:{rule.ListenPort} -> {rule.ToHost}:{rule.ToPort}");
        return streamer;
    }

    // Fire-and-forget the per-connection relay; RelayEgressAsync owns its own
    // error handling, so an unobserved fault cannot escape.
    private void StartEgressRelay(EgressRule rule, Stream guestStream, ConnectionLimiter limiter, CancellationToken cancellation) =>
        _ = RelayEgressAsync(rule, guestStream, limiter, cancellation);

    private async Task RelayEgressAsync(EgressRule rule, Stream guestStream, ConnectionLimiter limiter, CancellationToken cancellation)
    {
        Socket? socket = null;
        try
        {
            socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(rule.ToHost, rule.ToPort, cancellation);
            SocketKeepAlive.Apply(socket, policy.Keepalive);

            await using var hostStream = new NetworkStream(socket, ownsSocket: false);
            await RelayAsync(socket, hostStream, guestStream, cancellation);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            writeWarning?.Invoke(
                $"egress '{rule.Name}': connection to {rule.ToHost}:{rule.ToPort} failed: {ex.Message}");
        }
        finally
        {
            socket?.Dispose();
            await guestStream.DisposeAsync();
            limiter.Release();
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
        var limiter = new ConnectionLimiter(MaxConnectionsPerRule);
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                var client = await listener.AcceptSocketAsync(cancellation);
                if (!limiter.TryAcquire())
                {
                    writeWarning?.Invoke(
                        $"ingress '{rule.Name}': refused a connection, {MaxConnectionsPerRule} concurrent limit reached.");
                    client.Dispose();
                    continue;
                }

                _ = RelayIngressAsync(pfs, rule, client, limiter, cancellation);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (SocketException) { }
    }

    private async Task RelayIngressAsync(
        PortForwardingService pfs, IngressRule rule, Socket client, ConnectionLimiter limiter, CancellationToken cancellation)
    {
        using (client)
        {
            try
            {
                SocketKeepAlive.Apply(client, policy.Keepalive);

                var guestStream = await pfs.StreamToRemotePortAsync(rule.ToHost, rule.ToPort, cancellation);
                if (guestStream is null)
                    throw new IOException("the guest refused the connection (port forwarding is off).");

                await using (var hostStream = new NetworkStream(client, ownsSocket: false))
                await using (guestStream)
                    await RelayAsync(client, hostStream, guestStream, cancellation);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                writeWarning?.Invoke($"ingress '{rule.Name}': {ex.Message}");
            }
            finally
            {
                limiter.Release();
            }
        }
    }

    // Bidirectional relay between a host socket egs-tool owns and an SSH channel.
    // Both directions are pumped to completion (not torn down on the first EOF), so
    // a host caller that half-closes its send side after a request still receives
    // the full response. When the guest side ends, the host socket's send is
    // half-closed so the caller observes EOF while the other direction keeps
    // draining. (SSH channels here have no half-close, so a host-side EOF cannot be
    // signalled to the guest without a full close; a protocol that waits for the
    // caller's EOF before replying relies on session teardown to end.)
    private static async Task RelayAsync(
        Socket hostSocket, Stream hostStream, Stream guestStream, CancellationToken cancellation)
    {
        // When the SSH channel fully CLOSES, the host->guest pump may still be blocked
        // reading from a target that holds its connection open; dispose the host socket
        // so that read unblocks and the relay ends. This fires on channel close, not a
        // one-direction EOF, so a caller that half-closed its send side still receives
        // the full response.
        var channel = (guestStream as SshStream)?.Channel;
        void OnChannelClosed(object? s, SshChannelClosedEventArgs e)
        {
            try { hostSocket.Dispose(); }
            catch { /* already disposed */ }
        }

        if (channel is not null)
            channel.Closed += OnChannelClosed;

        try
        {
            var hostToGuest = PumpAsync(hostStream, guestStream, null, cancellation);
            var guestToHost = PumpAsync(guestStream, hostStream, () => TryShutdownSend(hostSocket), cancellation);
            await Task.WhenAll(hostToGuest, guestToHost);
        }
        finally
        {
            if (channel is not null)
                channel.Closed -= OnChannelClosed;
        }
    }

    private static async Task PumpAsync(Stream from, Stream to, Action? onCompleted, CancellationToken cancellation)
    {
        try
        {
            await from.CopyToAsync(to, cancellation);
        }
        catch
        {
            // Peer closed, cancelled, or the paired direction tore the streams down.
        }
        finally
        {
            try { onCompleted?.Invoke(); }
            catch { /* socket already closed */ }
        }
    }

    private static void TryShutdownSend(Socket socket)
    {
        try { socket.Shutdown(SocketShutdown.Send); }
        catch { /* already closed / not connected */ }
    }

    private sealed class ListenerHandle(TcpListener listener) : IDisposable
    {
        public void Dispose() => listener.Stop();
    }

    // Bounds concurrent data-plane connections for one rule. TryAcquire admits a
    // connection only while the active count is below the cap; Release is called
    // once per admitted connection when its relay ends.
    private sealed class ConnectionLimiter(int max)
    {
        private int _active;

        public bool TryAcquire()
        {
            if (Interlocked.Increment(ref _active) <= max)
                return true;

            Interlocked.Decrement(ref _active);
            return false;
        }

        public void Release() => Interlocked.Decrement(ref _active);
    }
}
