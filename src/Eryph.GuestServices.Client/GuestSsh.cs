using System.Diagnostics;
using System.Security.Claims;
using Microsoft.DevTunnels.Ssh;
using Microsoft.DevTunnels.Ssh.Algorithms;
using Microsoft.DevTunnels.Ssh.Events;
using Microsoft.DevTunnels.Ssh.Tcp;

namespace Eryph.GuestServices.Client;

// The transport-neutral half of establishing a guest session: given an open
// bidirectional stream and the client key, run the SSH connect + public-key
// authentication that every transport shares.
//
// The server host key is trusted unconditionally. Both transports already pin
// the endpoint out-of-band: the Hyper-V socket is an admin-only local channel,
// and the eryph channel is an authenticated, encrypted relay to a specific
// catlet. In neither case is there a host key the operator could have known in
// advance, which mirrors the StrictHostKeyChecking=no the generated ssh_config
// uses for the same reason.
internal static class GuestSsh
{
    // Builds the client session configuration. enablePortForwarding registers the
    // PortForwardingService so the proxied-tunnel engine can activate it — without
    // this, ActivateService<PortForwardingService>() throws KeyNotFoundException and
    // the tunnel cannot start. The default leaves shell/transfer callers untouched.
    internal static SshSessionConfiguration BuildClientConfiguration(
        int keepAliveTimeoutSeconds, bool enablePortForwarding)
    {
        var config = new SshSessionConfiguration();
        if (keepAliveTimeoutSeconds > 0)
            config.KeepAliveTimeoutInSeconds = keepAliveTimeoutSeconds;
        if (enablePortForwarding)
            config.Services.Add(typeof(PortForwardingService), null);
        return config;
    }

    // keepAliveTimeoutSeconds > 0 turns on the SSH control-channel keepalive: the
    // session sends a periodic keepalive request so a long-idle tunnel stays up and
    // a dead peer is detected (KeepAliveFailed). 0 keeps the shell/transfer callers
    // exactly as they were. enablePortForwarding registers + pre-hardens the
    // PortForwardingService for the proxied-tunnel path.
    public static async Task<SshClientSession> EstablishSessionAsync(
        Stream transport, IKeyPair keyPair, int keepAliveTimeoutSeconds = 0, bool enablePortForwarding = false)
    {
        var config = BuildClientConfiguration(keepAliveTimeoutSeconds, enablePortForwarding);
        var session = new SshClientSession(config, new TraceSource("Client"));
        session.Authenticating += (_, e) =>
        {
            if (e.AuthenticationType == SshAuthenticationType.ServerPublicKey)
                e.AuthenticationTask = Task.FromResult<ClaimsPrincipal?>(new ClaimsPrincipal());
        };

        if (enablePortForwarding)
        {
            // Pin the client's forwarding posture BEFORE connecting so a malicious
            // guest cannot open a channel in the window before the engine sets these.
            // The whole VM is untrusted: never accept a guest-initiated direct-tcpip
            // to a host port, and never open a library-owned local listener.
            var portForwarding = session.ActivateService<PortForwardingService>();
            portForwarding.AcceptRemoteConnectionsForNonForwardedPorts = false;
            portForwarding.AcceptLocalConnectionsForForwardedPorts = false;
        }

        try
        {
            await session.ConnectAsync(transport);
            var authenticated = await session.AuthenticateAsync(new SshClientCredentials("egs", keyPair));
            if (!authenticated)
                throw new GuestConnectionException("Could not connect. The authentication failed.");
        }
        catch
        {
            // The caller owns the transport stream and disposes it on failure; here
            // we only have to drop the half-built session so it does not leak.
            session.Dispose();
            throw;
        }

        return session;
    }
}
