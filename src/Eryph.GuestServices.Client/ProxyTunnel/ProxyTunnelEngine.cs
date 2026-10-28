using System.Buffers;
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

    // Deadline for one connect + SSH handshake attempt (no keepalive exists yet).
    private static readonly TimeSpan SetupTimeout = TimeSpan.FromSeconds(60);

    // Runs until cancelled. Each session plan reconnects independently; a rule the
    // guest refuses (port forwarding off) fails closed and cancels the whole tunnel.
    public async Task RunAsync(CancellationToken cancellation)
    {
        var plans = PlanSessions();

        using var scope = CancellationTokenSource.CreateLinkedTokenSource(cancellation);

        // Track how many plans are CURRENTLY established (not how many ever connected),
        // so "tunnel is up" reflects the real state: it is announced on the rising edge
        // to all-up, and again if a reconnect later restores the last missing plan.
        var currentlyUp = 0;
        void OnPlanUp()
        {
            if (Interlocked.Increment(ref currentlyUp) == plans.Count)
                writeInfo?.Invoke(
                    $"Tunnel '{policy.Tunnel}' is up: {plans.Count} session(s), "
                    + $"{policy.Egress.Count} egress, {policy.Ingress.Count} ingress. Press Ctrl+C to stop.");
        }

        void OnPlanDown() => Interlocked.Decrement(ref currentlyUp);

        ExceptionDispatchInfo? fatal = null;
        async Task RunOne(SessionPlan plan)
        {
            try
            {
                await RunPlanAsync(plan, OnPlanUp, OnPlanDown, scope.Token);
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

    private async Task RunPlanAsync(SessionPlan plan, Action onPlanUp, Action onPlanDown, CancellationToken cancellation)
    {
        var everEstablished = false;
        var attempt = 0;

        while (!cancellation.IsCancellationRequested)
        {
            var sessionStart = DateTimeOffset.UtcNow;
            string? failure = null; // null => the session returned (a clean drop)
            try
            {
                await RunSessionAsync(
                    plan,
                    () =>
                    {
                        everEstablished = true;
                        sessionStart = DateTimeOffset.UtcNow;
                    },
                    onPlanUp,
                    onPlanDown,
                    cancellation);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                break;
            }
            catch (TunnelPolicyException)
            {
                throw; // fail closed — do not reconnect into a weaker setup
            }
            catch (Exception ex)
            {
                // A dropped/aborted setup (session scope cancelled mid-establish → OCE),
                // a transient connect error, or a mid-run fault — all reconnectable.
                failure = ex.Message;
            }

            if (cancellation.IsCancellationRequested)
                break;

            // Count any attempt that never reached "established" — whether it threw or
            // returned after a drop right after setup — toward the startup limit, so a
            // guest that always drops before establishing cannot retry forever.
            if (!everEstablished && attempt >= StartupRetryLimit)
                throw new TunnelPolicyException(
                    $"The tunnel '{policy.Tunnel}' could not be established"
                    + (failure is null ? "." : $": {failure}."));

            writeWarning?.Invoke(
                everEstablished
                    ? $"Tunnel '{policy.Tunnel}' dropped{(failure is null ? "" : $": {failure}")}. Reconnecting…"
                    : $"Tunnel '{policy.Tunnel}' not up yet{(failure is null ? "" : $": {failure}")}. Retrying…");

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

    private async Task<GuestSshConnection> ConnectWithDeadlineAsync(CancellationToken cancellation)
    {
        using var connectDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        connectDeadline.CancelAfter(SetupTimeout);

        // Pass the deadline token to the connector (the SSH handshake and the compute
        // calls that accept it honor it). But some control-plane steps — e.g. token
        // minting — ignore cancellation, so also bound the WAIT with a hard timeout:
        // if the deadline wins, surface a reconnectable error and leave the attempt to
        // finish detached, disposing it if it later succeeds, so nothing leaks.
        var connectTask = connector.ConnectAsync(connectDeadline.Token);
        var timeout = Task.Delay(SetupTimeout, cancellation);

        if (await Task.WhenAny(connectTask, timeout) == timeout)
        {
            // Install the late-result cleanup FIRST — whether we leave because the
            // deadline fired or because the caller/a sibling cancelled, a connect that
            // completes afterwards must still have its session + transport disposed.
            _ = connectTask.ContinueWith(
                t => { if (t.Status == TaskStatus.RanToCompletion) _ = t.Result.DisposeAsync(); },
                TaskScheduler.Default);
            cancellation.ThrowIfCancellationRequested(); // caller stopped us, not the deadline
            throw new IOException($"the connection did not complete within {SetupTimeout.TotalSeconds:0}s");
        }

        try
        {
            return await connectTask;
        }
        catch (OperationCanceledException) when (connectDeadline.IsCancellationRequested && !cancellation.IsCancellationRequested)
        {
            throw new IOException($"the connection did not complete within {SetupTimeout.TotalSeconds:0}s");
        }
    }

    private async Task RunSessionAsync(
        SessionPlan plan, Action markEstablished, Action onUp, Action onDown, CancellationToken cancellation)
    {
        await using var connection = await ConnectWithDeadlineAsync(cancellation);
        var session = connection.Session;

        PortForwardingService pfs;
        try
        {
            pfs = session.ActivateService<PortForwardingService>();
        }
        catch (KeyNotFoundException)
        {
            // The connector produced a session without PortForwardingService
            // registered (a programming error, not a network condition) — fail fast
            // rather than retry.
            throw new TunnelPolicyException(
                "Internal error: the SSH session was not configured for port forwarding.");
        }

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
        var reportedUp = false;
        try
        {
            await EnsureGateAsync(pfs, sessionScope.Token);

            foreach (var rule in plan.Egress)
                teardown.Add(await EstablishEgressAsync(pfs, rule, sessionScope.Token));

            foreach (var rule in plan.Ingress)
                teardown.Add(StartIngressListener(pfs, rule, sessionScope.Token));

            // If the session already dropped while we were still setting up, do not
            // report it as established — let the plan reconnect (and keep counting
            // toward the startup limit) instead of latching everEstablished.
            if (!sessionScope.IsCancellationRequested)
            {
                markEstablished();
                onUp();          // this plan is now established (current-state count)
                reportedUp = true;
            }

            await using (cancellation.Register(() => dropped.TrySetResult()))
                await dropped.Task;
        }
        finally
        {
            if (reportedUp)
                onDown();        // this plan is no longer established
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
        RemotePortStreamer? streamer;
        try
        {
            streamer = await pfs.StreamFromRemotePortAsync(rule.ListenAddress, rule.ListenPort, sessionToken);
        }
        catch (ArgumentException)
        {
            // The library indexes forwards by port: a guest that echoes a port already
            // bound by another rule trips an ArgumentException after the request
            // succeeded. Fail closed rather than retry a policy the guest is fighting.
            throw new TunnelPolicyException(
                $"egress rule '{rule.Name}': the guest echoed a port already bound by another rule.");
        }

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

        // Cancel the outbound dial if the guest closes its channel while we are still
        // connecting: otherwise a target that drops connection attempts would pin the
        // socket and the limiter slot until the OS connect timeout, and 128 such
        // attempts would block the rule.
        var guestChannel = (guestStream as SshStream)?.Channel;
        using var dialScope = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        void OnChannelClosed(object? s, SshChannelClosedEventArgs e) => Cancel(dialScope);
        if (guestChannel is not null)
        {
            guestChannel.Closed += OnChannelClosed;
            if (guestChannel.IsClosed) Cancel(dialScope);
        }

        try
        {
            socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(rule.ToHost, rule.ToPort, dialScope.Token);
            SocketKeepAlive.Apply(socket, policy.Keepalive);

            await using var hostStream = new NetworkStream(socket, ownsSocket: false);
            // Egress: the host socket is the dialed target — the server.
            await RelayAsync(socket, hostStream, guestStream, hostIsServer: true, cancellation);
        }
        catch (OperationCanceledException)
        {
            // The dial was cancelled (session stopping or the guest closed the channel
            // mid-connect) — a clean teardown, not a failure to report.
        }
        catch (Exception ex)
        {
            writeWarning?.Invoke(
                $"egress '{rule.Name}': connection to {rule.ToHost}:{rule.ToPort} failed: {ex.Message}");
        }
        finally
        {
            if (guestChannel is not null) guestChannel.Closed -= OnChannelClosed;
            socket?.Dispose();
            // Release the slot even if disposing the (already torn-down) guest stream
            // throws — otherwise a guest could leak the rule's connection slots.
            try { await guestStream.DisposeAsync(); }
            catch { /* channel/session already gone */ }
            limiter.Release();
        }
    }

    private IDisposable StartIngressListener(PortForwardingService pfs, IngressRule rule, CancellationToken sessionToken)
    {
        var listener = new TcpListener(rule.Listen);
        // Refuse to share the bind: another local process must not be able to
        // SO_REUSEADDR onto this ingress port and steal connections into the guest.
        listener.ExclusiveAddressUse = true;
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
        while (!cancellation.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await listener.AcceptSocketAsync(cancellation);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break; // the listener was stopped during teardown
            }
            catch (SocketException ex)
            {
                // A client that aborts mid-handshake can fault one Accept (e.g.
                // ConnectionReset on Windows); that must not kill the whole listener.
                // Pause briefly so a persistent fault (e.g. resource shortage) cannot
                // spin the loop and flood warnings.
                writeWarning?.Invoke($"ingress '{rule.Name}': accept failed: {ex.Message}");
                try { await Task.Delay(100, cancellation); }
                catch (OperationCanceledException) { break; }
                continue;
            }

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
                    // Ingress: the host socket is the accepted caller — the client.
                    await RelayAsync(client, hostStream, guestStream, hostIsServer: false, cancellation);
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

    private const int RelayBufferSize = 81920;
    private static readonly TimeSpan DrainDeadline = TimeSpan.FromSeconds(30);

    // Bidirectional relay between a host socket egs-tool owns and an SSH channel to
    // the guest. One peer is the client, the other the server; hostIsServer tells
    // them apart (egress: the host socket is the dialed target = server; ingress: the
    // host socket is the accepted caller = client).
    //
    // Teardown cancels READS only — never writes. Cancelling a write on an SshStream
    // can interrupt a packet on the SHARED SSH session and disrupt other channels /
    // relays on it (DevTunnels' own forwarder avoids cancelling destination writes),
    // so an in-flight write is always allowed to finish; a write that cannot finish
    // ends when its stream is disposed on teardown.
    //
    // End-of-stream rules: the server's response direction ending ends the whole
    // relay; the client's request direction ending is a half-close, forwarded to the
    // server as a FIN only on a host socket (Shutdown(Send)) — the SSH channel has no
    // half-close. When the SSH channel closes, the host-side read is cancelled (there
    // is nothing to write to the gone guest) while the guest-side read drains any
    // buffered response to the host client, bounded by a drain deadline.
    private static async Task RelayAsync(
        Socket hostSocket, Stream hostStream, Stream guestStream, bool hostIsServer, CancellationToken cancellation)
    {
        // Separate read scopes: a channel close cancels only the host-side read.
        using var hostReadScope = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        using var guestReadScope = CancellationTokenSource.CreateLinkedTokenSource(cancellation);

        // Writes are never cancelled, so a pump can sit in a write a peer refuses to
        // consume. The drain deadline is the backstop: it force-closes BOTH the host
        // socket and the guest channel, which unblocks any such write, bounding how
        // long a connection (and its limiter slot) can linger during teardown.
        using var drainTimer = new CancellationTokenSource();
        void StartDrain()
        {
            try { drainTimer.CancelAfter(DrainDeadline); }
            catch (ObjectDisposedException) { }
        }

        drainTimer.Token.Register(() =>
        {
            try { hostSocket.Dispose(); } catch { /* already gone */ }
            try { guestStream.Dispose(); } catch { /* already gone */ }
            Cancel(hostReadScope);
            Cancel(guestReadScope);
        });

        // Terminal: cancel both reads and arm the drain so a stuck write cannot hang
        // Task.WhenAll (and hold the connection-limit slot) indefinitely.
        void EndRelay() { Cancel(hostReadScope); Cancel(guestReadScope); StartDrain(); }

        var channel = (guestStream as SshStream)?.Channel;
        void OnClosed()
        {
            // Guest gone: stop reading from the host (nothing to deliver), but let the
            // guest->host read drain buffered response bytes under the drain deadline.
            Cancel(hostReadScope);
            StartDrain();
        }

        void OnChannelClosed(object? s, SshChannelClosedEventArgs e) => OnClosed();
        if (channel is not null)
        {
            channel.Closed += OnChannelClosed;
            // Race: the channel may have closed before we subscribed — the event
            // would not replay.
            if (channel.IsClosed)
                OnClosed();
        }

        try
        {
            // hostReading: host -> guest.  guestReading: guest -> host.
            var hostReading = PumpAsync(hostStream, guestStream, hostReadScope.Token,
                onEof: hostIsServer ? EndRelay : null,          // egress host=server->client ends; ingress host=client->server half-closes
                onError: EndRelay);
            var guestReading = PumpAsync(guestStream, hostStream, guestReadScope.Token,
                onEof: hostIsServer
                    ? () => TryShutdown(hostSocket, SocketShutdown.Send)                       // egress guest=client->server: FIN the host server
                    : () => { TryShutdown(hostSocket, SocketShutdown.Send); EndRelay(); },     // ingress guest=server->client: FIN the host client, then end
                onError: EndRelay);

            await Task.WhenAll(hostReading, guestReading);
        }
        finally
        {
            if (channel is not null)
                channel.Closed -= OnChannelClosed;
        }
    }

    // Copies one direction. The READ honors readToken (cancelled on teardown); the
    // WRITE runs uncancelled so an SSH packet is never cut mid-flight. onEof fires on
    // a clean end-of-stream; onError aborts the pair on an I/O fault.
    private static async Task PumpAsync(Stream from, Stream to, CancellationToken readToken, Action? onEof, Action onError)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(RelayBufferSize);
        try
        {
            while (true)
            {
                int read;
                try
                {
                    read = await from.ReadAsync(buffer.AsMemory(0, RelayBufferSize), readToken);
                }
                catch (OperationCanceledException)
                {
                    return; // teardown — reads are cancellable
                }

                if (read == 0)
                {
                    try { onEof?.Invoke(); }
                    catch { /* socket already closed */ }
                    return;
                }

                await to.WriteAsync(buffer.AsMemory(0, read), CancellationToken.None);
            }
        }
        catch
        {
            // A read I/O fault (not cancellation) or a write fault: abort the pair so
            // Task.WhenAll cannot hang and the connection-limit slot is released.
            onError();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void TryShutdown(Socket socket, SocketShutdown how)
    {
        try { socket.Shutdown(how); }
        catch { /* already closed / not connected */ }
    }

    private static void Cancel(CancellationTokenSource cts)
    {
        try { cts.Cancel(); }
        catch (ObjectDisposedException) { }
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
