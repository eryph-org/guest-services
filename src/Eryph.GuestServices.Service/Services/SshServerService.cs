using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Claims;
using Eryph.GuestServices.Core;
using Eryph.GuestServices.DevTunnels.Ssh.Extensions;
using Eryph.GuestServices.DevTunnels.Ssh.Extensions.Services;
using Eryph.GuestServices.HvDataExchange.Guest;
using Eryph.GuestServices.Sockets;
using Microsoft.DevTunnels.Ssh;
using Microsoft.DevTunnels.Ssh.Algorithms;
using Microsoft.DevTunnels.Ssh.Events;
using Microsoft.DevTunnels.Ssh.Messages;
using Microsoft.DevTunnels.Ssh.Tcp;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Eryph.GuestServices.Service.Services;

internal sealed class SshServerService(
    IKeyStorage keyStorage,
    IHostKeyGenerator hostKeyGenerator,
    IGuestDataExchange guestDataExchange,
    IClientKeyProvider clientKeyProvider,
    IShellSelector shellSelector,
    IServiceControlFlags controlFlags,
    ILogger<SshServerService> logger) : IHostedService, IAsyncDisposable
{
    private Socket? _socket;
    private SocketSshServer? _server;
    private Task? _listenTask;
    private bool _started;
    private bool _portForwardingEnabled;
    private bool _gatewayPortsEnabled;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await Task.Yield();

        if (!controlFlags.IsRemoteAccessEnabled())
        {
            logger.LogInformation(
                "Remote access disabled via registry (HKLM\\SOFTWARE\\eryph\\guest-services\\RemoteAccessEnabled=0); SSH transport not started.");
            return;
        }

        _started = true;
        _portForwardingEnabled = controlFlags.IsPortForwardingEnabled();
        _gatewayPortsEnabled = controlFlags.IsGatewayPortsEnabled();

        var hostKey = await keyStorage.GetHostKeyAsync();
        if (hostKey is null)
        {
            logger.LogInformation("No host key found. Generating a new one.");
            hostKey = hostKeyGenerator.GenerateHostKey();
            await keyStorage.SetHostKeyAsync(hostKey);
        }

        var config = BuildConfiguration(shellSelector, _portForwardingEnabled);
        if (_portForwardingEnabled)
            logger.LogInformation(
                "Port forwarding enabled (PortForwardingEnabled); SSH tunneling (-L/-R) is available. "
                + "Reverse-forward (-R) listeners bind {BindScope}.",
                _gatewayPortsEnabled
                    ? "the client-requested address (GatewayPorts enabled)"
                    : "loopback only — 127.0.0.1 / ::1 (GatewayPorts disabled)");

        _server = new SocketSshServer(config, new TraceSource("SshServer"));
        _server.Credentials = new SshServerCredentials(hostKey);
        _server.SessionAuthenticating += SessionAuthenticating;
        _server.SessionRequest += SessionRequesting;
        _server.ExceptionRaised += ExceptionRaised;

        _socket = await SocketFactory.CreateServerSocket(ListenMode.Parent, Constants.ServiceId, 1);
        _listenTask = _server.AcceptSessionsAsync(_socket);

        await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        await SetStatusAsync("available");
        logger.LogInformation("SSH transport listening on the Hyper-V socket; remote access is available.");
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // When remote access is disabled the transport was never started: no
        // listener, no socket, and no status was advertised. Skip the teardown
        // (including the KVP status clear) so a never-started service is safe to
        // stop.
        if (!_started)
            return;

        await SetStatusAsync(null);
        _server?.Dispose();
        if (_listenTask is not null)
            await _listenTask.WaitAsync(cancellationToken);

        _socket?.Dispose();
    }

    private void ExceptionRaised(object? sender, Exception e)
    {
        logger.LogWarning(e, "Unhandled exception in the SSH server.");
    }

    private void SessionRequesting(object? sender, SshRequestEventArgs<SessionRequestMessage> e) =>
        ApplySessionRequestPolicy(
            e,
            _portForwardingEnabled,
            _gatewayPortsEnabled,
            () => (sender as SshSession)?.GetService<PortForwardingService>(),
            logger);

    /// <summary>
    /// The reverse-forward (<c>ssh -R</c>) session-request policy. Unlike the
    /// <c>direct-tcpip</c> channel of <c>ssh -L</c> — which the registered
    /// <see cref="PortForwardingService"/> authorizes on its own — a
    /// <c>tcpip-forward</c> session request is refused unless it is explicitly
    /// authorized here (its <c>IsAuthorized</c> defaults to false).
    /// </summary>
    /// <remarks>
    /// For <c>tcpip-forward</c> with <c>GatewayPorts</c> off (the default), the
    /// listener must be pinned to loopback. The client's requested bind address is
    /// already resolved by the <see cref="PortForwardingService"/> before this
    /// runs, and the request message is backed by an immutable wire buffer, so the
    /// address cannot be rewritten; instead a loopback-forcing
    /// <see cref="PortForwardingService.TcpListenerFactory"/> is installed, which
    /// the library reads later when it binds the listener. The pin is applied
    /// <b>before</b> authorizing and the request is refused (fail <b>closed</b>) if
    /// the factory cannot be installed — never authorized-but-unpinned, which would
    /// let the library bind <see cref="System.Net.IPAddress.Any"/> and expose the
    /// guest on every interface. <paramref name="resolvePortForwarding"/> is only
    /// invoked for a <c>tcpip-forward</c> that needs pinning.
    /// </remarks>
    internal static void ApplySessionRequestPolicy(
        SshRequestEventArgs<SessionRequestMessage> e,
        bool portForwardingEnabled,
        bool gatewayPortsEnabled,
        Func<PortForwardingService?> resolvePortForwarding,
        ILogger logger)
    {
        if (!ShouldAuthorizeSessionRequest(portForwardingEnabled, e.RequestType))
            return;

        // cancel-tcpip-forward only releases a previously requested listener;
        // there is nothing to bind, so no loopback pin is needed.
        if (e.RequestType == Constants.CancelTcpipForwardRequestType)
        {
            e.IsAuthorized = true;
            return;
        }

        // tcpip-forward with GatewayPorts opted in: honor the client-requested
        // bind address (the library's default factory), accepting the exposure.
        if (gatewayPortsEnabled)
        {
            e.IsAuthorized = true;
            return;
        }

        // tcpip-forward, GatewayPorts off (default): pin the listener to loopback
        // BEFORE authorizing, and fail CLOSED — refuse the forward if the guard
        // cannot be installed, rather than let the library bind IPAddress.Any.
        var portForwarding = resolvePortForwarding();
        if (portForwarding is null)
        {
            logger.LogWarning(
                "Refusing ssh -R (tcpip-forward): the PortForwardingService could not be resolved, "
                + "so the loopback bind guard could not be installed. Enable GatewayPorts to allow "
                + "non-loopback reverse forwards.");
            return;
        }

        portForwarding.TcpListenerFactory = LoopbackTcpListenerFactory.Instance;
        e.IsAuthorized = true;
    }

    /// <summary>
    /// Decides whether a session-level global request may be authorized at all:
    /// the reverse-forward requests (<c>tcpip-forward</c> / <c>cancel-tcpip-forward</c>
    /// for <c>ssh -R</c>) when, and only when, port forwarding is enabled.
    /// </summary>
    internal static bool ShouldAuthorizeSessionRequest(bool portForwardingEnabled, string? requestType) =>
        portForwardingEnabled
        && (requestType == Constants.TcpipForwardRequestType
            || requestType == Constants.CancelTcpipForwardRequestType);

    private void SessionAuthenticating(object? _, SshAuthenticatingEventArgs e)
    {
        // Per-attempt protocol step (the public-key query probes before the real
        // signature, so this fires several times per connection): Debug, not
        // Information, to keep the operational log readable.
        logger.LogDebug("Authenticating SSH session ({AuthType}).", e.AuthenticationType);
        if (e.AuthenticationType is not (SshAuthenticationType.ClientPublicKey or SshAuthenticationType.ClientPublicKeyQuery))
            return;

        if (e.Username != "egs")
        {
            // A rejected connection is security-relevant — Warning so it surfaces
            // even when the Event Log / sinks are filtered to Warning+.
            logger.LogWarning("SSH authentication rejected: unexpected user name '{Username}'.", e.Username);
            return;
        }

        if (e.PublicKey is null)
        {
            logger.LogDebug("SSH authentication attempt for user {Username} carried no public key.", e.Username);
            return;
        }

        e.AuthenticationTask = CheckClientKey(e.PublicKey);
    }

    // internal for composition-root tests in Eryph.GuestServices.Service.Tests
    internal async Task<ClaimsPrincipal?> CheckClientKey(IKeyPair clientKey)
    {
        if (!await clientKeyProvider.IsAuthorizedAsync(clientKey))
        {
            logger.LogWarning(
                "SSH authentication failed: public key {FingerPrint} is not authorized.",
                clientKey.GetFingerPrint());
            return null;
        }

        // A successful remote login is the event most worth auditing — record it
        // (with the key fingerprint) at Information.
        logger.LogInformation(
            "SSH client authenticated with key {FingerPrint}.",
            clientKey.GetFingerPrint());
        return new ClaimsPrincipal();
    }

    public async ValueTask DisposeAsync()
    {
        if (!_started)
            return;

        await SetStatusAsync(null);
        _server?.Dispose();
        _socket?.Dispose();
    }

    /// <summary>
    /// Builds the SSH session configuration: the fixed set of egs services
    /// (subsystem / shell / exec / file transfer / directory listing) plus,
    /// only when <paramref name="portForwardingEnabled"/> is set, the
    /// <see cref="PortForwardingService"/> that handles <c>direct-tcpip</c>
    /// (<c>ssh -L</c>) and <c>tcpip-forward</c> (<c>ssh -R</c>) requests. Leaving
    /// the service unregistered means the server rejects every forwarding
    /// request, so forwarding is off unless an operator opts in.
    /// </summary>
    internal static SshSessionConfiguration BuildConfiguration(
        IShellSelector shellSelector, bool portForwardingEnabled)
    {
        var config = new SshSessionConfiguration(useSecurity: true);

        config.Services.Add(typeof(SubsystemService), null);
        // The shell selector is passed as the service activation config object.
        // DevTunnels.Ssh activates the matching 2-arg ctor. Both the interactive
        // ShellService and the exec CommandService run the client request through
        // the selected shell, so they share the selector.
        config.Services.Add(typeof(CommandService), shellSelector);
        config.Services.Add(typeof(ShellService), shellSelector);
        config.Services.Add(typeof(UploadFileService), null);
        config.Services.Add(typeof(DownloadFileService), null);
        config.Services.Add(typeof(ListDirectoryService), null);

        if (portForwardingEnabled)
            config.Services.Add(typeof(PortForwardingService), null);

        return config;
    }

    /// <summary>
    /// The optional features advertised to the host via <see cref="Constants.FeaturesKey"/>.
    /// Port forwarding is only listed when it is actually enabled, so a client
    /// can tell whether <c>-L</c>/<c>-R</c> will be honored.
    /// </summary>
    internal static string[] GetSupportedFeatures(bool portForwardingEnabled) =>
        portForwardingEnabled
            ? [Constants.ShellOverrideFeature, Constants.PortForwardingFeature]
            : [Constants.ShellOverrideFeature];

    private async Task SetStatusAsync(string? status)
    {
        // FeaturesKey is cleared on shutdown so capability discovery stays
        // honest after a downgrade. A pre-features service does not write
        // this key, so without explicit removal the previous version's
        // feature list would remain visible to the host and make tools
        // think a feature is supported when it isn't.
        var values = new Dictionary<string, string?>
        {
            [Constants.StatusKey] = status,
            [Constants.VersionKey] = GitVersionInformation.SemVer,
            [Constants.OperatingSystemKey] = RuntimeInformation.OSDescription,
            [Constants.FeaturesKey] = status is null
                ? null
                : string.Join(' ', GetSupportedFeatures(_portForwardingEnabled)),
        };
        await guestDataExchange.SetGuestValuesAsync(values);
    }
}
