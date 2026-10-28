using System.Net;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using Microsoft.DevTunnels.Ssh;
using Microsoft.DevTunnels.Ssh.Events;
using Microsoft.DevTunnels.Ssh.Tcp;

namespace Eryph.GuestServices.Client.ProxyTunnel;

// Runs a proxied tunnel in-process: opens eryph/SSH channels through the supplied
// connector, and for every policy rule forwards bytes between a data-plane socket
// egs-tool owns and an SSH channel. No external forwarder and no listener beyond
// what the policy declares.
//
// egs-tool, not the DevTunnels library, owns the data plane:
//   * ingress: egs-tool opens its own host listener and relays each connection as a
//     direct-tcpip channel to the rule's single fixed target inside the guest.
//   * egress: egs-tool registers a guest-side loopback listener (tcpip-forward) and,
//     for each forwarded connection, dials the rule's single fixed host target with
//     its own socket.
//
// Multi-session: DevTunnels keys reverse-forwards by port alone, so two egress rules
// on the same port cannot share one SSH session even on different loopback addresses.
// Such rules are split across separate sessions (one eryph channel each), each
// binding its own guest loopback address. Ingress has no such constraint and rides
// the first session.
//
// Security pinning (the whole VM, incl. egs-service, is untrusted):
//   * AcceptRemoteConnectionsForNonForwardedPorts = false — never accept a
//     guest-initiated direct-tcpip channel to an arbitrary host-local port. This is
//     THE control; ForwardConnectionsToLocalPorts must stay at its default (true),
//     both because the egress RemotePortStreamer rides that path and because this pin
//     is what stops a guest-steered dial.
//   * AcceptLocalConnectionsForForwardedPorts = false — no library-owned listener.
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

    // How long a session must stay up before the next drop resets the backoff.
    private static readonly TimeSpan StableThreshold = TimeSpan.FromSeconds(30);

    // Transient failures before a session has ever come up are retried this many
    // times before giving up and reporting startup failure.
    private const int StartupRetryLimit = 5;

    // Runs until cancelled. Each session plan reconnects independently; a rule the
    // guest refuses (port forwarding off) fails closed and cancels the whole tunnel.
    public async Task RunAsync(CancellationToken cancellation)
    {
        var plans = PlanSessions();

        using var scope = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var pending = plans.Count;
        void OnPlanFirstEstablished()
        {
            if (Interlocked.Decrement(ref pending) == 0)
                writeInfo?.Invoke(
                    $"Tunnel '{policy.Tunnel}' is up: {plans.Count} session(s), "
                    + $"{policy.Egress.Count} egress, {policy.Ingress.Count} ingress. Press Ctrl+C to stop.");
        }

        ExceptionDispatchInfo? fatal = null;
        async Task RunOne(SessionPlan plan)
        {
            try
            {
                await RunPlanAsync(plan, OnPlanFirstEstablished, scope.Token);
            }
            catch (OperationCanceledException) when (scope.IsCancellationRequested)
            {
                // Cancelled because a sibling plan failed, or the caller stopped us.
            }
            catch (Exception ex)
            {
                // A plan failed closed: record the first such failure, stop the rest.
                Interlocked.CompareExchange(ref fatal, ExceptionDispatchInfo.Capture(ex), null);
                await scope.CancelAsync();
                throw;
            }
        }

        var tasks = plans.Select(RunOne).ToArray();
        try
        {
            await Task.WhenAll(tasks);
        }
        catch
        {
            // Swallow here; the captured fatal (if any) is rethrown below with its
            // original stack. A plain cancellation leaves fatal null.
        }

        // A fail-closed refusal in any plan propagates; a plain stop returns normally.
        fatal?.Throw();
    }

    // Partitions egress rules so no two in a session share a port; ingress rides the
    // first session. The policy loader guarantees (address, port) uniqueness, so two
    // rules sharing a port necessarily differ in bind address and land in different
    // sessions binding different guest loopback addresses.
    private List<SessionPlan> PlanSessions()
    {
        var groups = GroupEgressByDistinctPorts(policy.Egress);
        if (groups.Count == 0)
            groups.Add([]);

        var plans = new List<SessionPlan>();
        for (var i = 0; i < groups.Count; i++)
            plans.Add(new SessionPlan(groups[i], i == 0 ? policy.Ingress : []));

        return plans;
    }

    // Groups egress rules so that no group contains two rules on the same port
    // (DevTunnels keys reverse-forwards by port, so same-port rules must land in
    // different SSH sessions). Greedy first-fit keeps the session count minimal:
    // it equals the highest number of rules sharing any one port.
    internal static List<List<EgressRule>> GroupEgressByDistinctPorts(IReadOnlyList<EgressRule> egress)
    {
        var groups = new List<List<EgressRule>>();
        var groupPorts = new List<HashSet<int>>();

        foreach (var rule in egress)
        {
            var index = -1;
            for (var i = 0; i < groups.Count; i++)
            {
                if (!groupPorts[i].Contains(rule.ListenPort)) { index = i; break; }
            }

            if (index < 0)
            {
                groups.Add([]);
                groupPorts.Add([]);
                index = groups.Count - 1;
            }

            groups[index].Add(rule);
            groupPorts[index].Add(rule.ListenPort);
        }

        return groups;
    }

    private async Task RunPlanAsync(SessionPlan plan, Action onFirstEstablished, CancellationToken cancellation)
    {
        var everEstablished = false;
        var firstReported = false;
        var attempt = 0;

        while (!cancellation.IsCancellationRequested)
        {
            var sessionStart = DateTimeOffset.UtcNow;
            try
            {
                await RunSessionAsync(
                    plan,
                    () =>
                    {
                        everEstablished = true;
                        sessionStart = DateTimeOffset.UtcNow;
                        if (!firstReported)
                        {
                            firstReported = true;
                            onFirstEstablished();
                        }
                    },
                    cancellation);

                writeWarning?.Invoke($"Tunnel '{policy.Tunnel}' session dropped. Reconnecting…");
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                // The session dropped while still establishing (its scope was
                // cancelled by Closed/KeepAliveFailed). Treat as a reconnectable drop.
                writeWarning?.Invoke($"Tunnel '{policy.Tunnel}' dropped during setup. Reconnecting…");
            }
            catch (TunnelPolicyException)
            {
                throw; // fail closed — do not reconnect into a weaker setup
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

    private async Task RunSessionAsync(SessionPlan plan, Action markEstablished, CancellationToken cancellation)
    {
        await using var connection = await connector.ConnectAsync(cancellation);
        var session = connection.Session;

        var pfs = session.ActivateService<PortForwardingService>();
        pfs.AcceptRemoteConnectionsForNonForwardedPorts = false;
        pfs.AcceptLocalConnectionsForForwardedPorts = false;

        // A drop (Closed / KeepAliveFailed) cancels the session scope so that an
        // in-flight forwarding setup request unblocks and the plan can reconnect,
        // rather than hanging on a guest that stopped responding mid-setup.
        using var sessionScope = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var dropped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Drop()
        {
            dropped.TrySetResult();
            try { sessionScope.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        void OnClosed(object? s, SshSessionClosedEventArgs e) => Drop();
        void OnKeepAliveFailed(object? s, SshKeepAliveEventArgs e) => Drop();
        session.Closed += OnClosed;
        session.KeepAliveFailed += OnKeepAliveFailed;

        var teardown = new List<IDisposable>();
        try
        {
            await EnsureGateAsync(pfs, sessionScope.Token);

            foreach (var rule in plan.Egress)
                teardown.Add(await EstablishEgressAsync(pfs, rule, sessionScope.Token));

            foreach (var rule in plan.Ingress)
                teardown.Add(StartIngressListener(pfs, rule, sessionScope.Token));

            markEstablished();

            await using (cancellation.Register(() => dropped.TrySetResult()))
                await dropped.Task;
        }
        finally
        {
            session.Closed -= OnClosed;
            session.KeepAliveFailed -= OnKeepAliveFailed;
            try { sessionScope.Cancel(); }
            catch (ObjectDisposedException) { }
            foreach (var disposable in teardown)
            {
                try { disposable.Dispose(); }
                catch { /* best-effort teardown */ }
            }
        }

        cancellation.ThrowIfCancellationRequested();
    }

    // Fail-closed gate probe. An ephemeral reverse forward exercises the exact
    // tcpip-forward authorization an egress rule needs; when PortForwardingEnabled is
    // off the guest refuses it (null) and the whole tunnel is refused rather than
    // brought up partially. direct-tcpip (ingress) is gated by the same switch.
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
        PortForwardingService pfs, EgressRule rule, CancellationToken sessionToken)
    {
        var streamer = await pfs.StreamFromRemotePortAsync(rule.ListenAddress, rule.ListenPort, sessionToken);
        if (streamer is null)
            // The gate probe already proved forwarding is on, so a null here is a
            // busy/stale guest listener on this port — transient, let the reconnect
            // path retry rather than failing the whole tunnel.
            throw new IOException(
                $"egress rule '{rule.Name}': the guest port {rule.ListenAddress}:{rule.ListenPort} is not available (in use).");

        // Do not trust the port the guest echoes back: if it does not match what we
        // asked to forward, refuse rather than carry a rule whose audit line would lie.
        if (streamer.RemotePort != rule.ListenPort)
        {
            streamer.Dispose();
            throw new TunnelPolicyException(
                $"egress rule '{rule.Name}': the guest bound port {streamer.RemotePort}, not the requested {rule.ListenPort}.");
        }

        var limiter = new ConnectionLimiter(MaxConnectionsPerRule);
        streamer.StreamOpened += (_, stream) => OnEgressStreamOpened(rule, limiter, stream, sessionToken);
        writeInfo?.Invoke(
            $"egress '{rule.Name}': guest {rule.ListenAddress}:{rule.ListenPort} -> {rule.ToHost}:{rule.ToPort}");
        return streamer;
    }

    private void OnEgressStreamOpened(EgressRule rule, ConnectionLimiter limiter, Stream stream, CancellationToken sessionToken)
    {
        // A guest can open connections on its loopback listener at will; cap how many
        // we carry through the host at once for this rule, then relay the rest.
        if (!limiter.TryAcquire())
        {
            writeWarning?.Invoke(
                $"egress '{rule.Name}': refused a connection, {MaxConnectionsPerRule} concurrent limit reached.");
            _ = stream.DisposeAsync();
            return;
        }

        _ = RelayEgressAsync(rule, stream, limiter, sessionToken);
    }

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
    // On a clean EOF in one direction, the peer's write side is half-closed so the
    // other direction can keep draining (a caller that half-closed its send still
    // gets the full response). On an I/O failure, the paired direction is aborted so
    // the relay cannot hang and leak a connection-limit slot. When the SSH channel
    // closes, only the host socket's receive side is shut down, unblocking the
    // host->guest read without truncating in-flight guest->host data.
    //
    // (SSH channels here have no half-close, so a clean host-side EOF cannot be
    // signalled to the guest without a full close; a guest protocol that waits for
    // the caller's EOF before replying relies on the channel/session closing to end.)
    private static async Task RelayAsync(
        Socket hostSocket, Stream hostStream, Stream guestStream, CancellationToken cancellation)
    {
        using var relayScope = CancellationTokenSource.CreateLinkedTokenSource(cancellation);

        var channel = (guestStream as SshStream)?.Channel;
        void OnChannelClosed(object? s, SshChannelClosedEventArgs e) => TryShutdown(hostSocket, SocketShutdown.Receive);
        if (channel is not null)
        {
            channel.Closed += OnChannelClosed;
            // Guard the race where the channel closed while we were dialing the host,
            // before we subscribed — the event would not replay.
            if (channel.IsClosed)
                TryShutdown(hostSocket, SocketShutdown.Receive);
        }

        try
        {
            var hostToGuest = PumpAsync(hostStream, guestStream, relayScope, onEof: null);
            var guestToHost = PumpAsync(guestStream, hostStream, relayScope, onEof: () => TryShutdown(hostSocket, SocketShutdown.Send));
            await Task.WhenAll(hostToGuest, guestToHost);
        }
        finally
        {
            if (channel is not null)
                channel.Closed -= OnChannelClosed;
        }
    }

    private static async Task PumpAsync(Stream from, Stream to, CancellationTokenSource relayScope, Action? onEof)
    {
        try
        {
            await from.CopyToAsync(to, relayScope.Token);
            // Clean EOF from 'from': propagate it to the peer's write side if we can.
            try { onEof?.Invoke(); }
            catch { /* socket already closed */ }
        }
        catch (OperationCanceledException)
        {
            // The paired direction aborted, or the session was cancelled.
        }
        catch
        {
            // I/O failure on this leg: abort the paired direction so Task.WhenAll
            // cannot block and the connection-limit slot is released.
            try { await relayScope.CancelAsync(); }
            catch (ObjectDisposedException) { }
        }
    }

    private static void TryShutdown(Socket socket, SocketShutdown how)
    {
        try { socket.Shutdown(how); }
        catch { /* already closed / not connected */ }
    }

    private sealed record SessionPlan(IReadOnlyList<EgressRule> Egress, IReadOnlyList<IngressRule> Ingress);

    private sealed class ListenerHandle(TcpListener listener) : IDisposable
    {
        public void Dispose() => listener.Stop();
    }

    // Bounds concurrent data-plane connections for one rule.
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
