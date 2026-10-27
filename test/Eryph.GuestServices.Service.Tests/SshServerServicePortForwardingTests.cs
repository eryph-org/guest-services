using System.Diagnostics;
using System.Security.Claims;
using AwesomeAssertions;
using Eryph.GuestServices.Core;
using Eryph.GuestServices.DevTunnels.Ssh.Extensions;
using Eryph.GuestServices.Service.Services;
using Microsoft.DevTunnels.Ssh;
using Microsoft.DevTunnels.Ssh.Events;
using Microsoft.DevTunnels.Ssh.Messages;
using Microsoft.DevTunnels.Ssh.Tcp;
using Microsoft.Extensions.Logging.Abstractions;

namespace Eryph.GuestServices.Service.Tests;

/// <summary>
/// Guards the opt-in port-forwarding wiring in <see cref="SshServerService"/>:
/// the <see cref="PortForwardingService"/> is registered (and the feature
/// advertised) only when the flag is on, so the server rejects every
/// <c>-L</c>/<c>-R</c> request by default.
/// </summary>
public class SshServerServicePortForwardingTests
{
    [Fact]
    public void BuildConfiguration_Disabled_DoesNotRegisterPortForwardingService()
    {
        var config = SshServerService.BuildConfiguration(new StubShellSelector(), portForwardingEnabled: false);

        config.Services.Should().NotContainKey(typeof(PortForwardingService));
    }

    [Fact]
    public void BuildConfiguration_Enabled_RegistersPortForwardingService()
    {
        var config = SshServerService.BuildConfiguration(new StubShellSelector(), portForwardingEnabled: true);

        config.Services.Should().ContainKey(typeof(PortForwardingService));
    }

    [Fact]
    public void GetSupportedFeatures_Disabled_OmitsPortForwardingFeature()
    {
        var features = SshServerService.GetSupportedFeatures(portForwardingEnabled: false);

        features.Should().Contain(Constants.ShellOverrideFeature);
        features.Should().NotContain(Constants.PortForwardingFeature);
    }

    [Fact]
    public void GetSupportedFeatures_Enabled_AdvertisesPortForwardingFeature()
    {
        var features = SshServerService.GetSupportedFeatures(portForwardingEnabled: true);

        features.Should().Contain(Constants.ShellOverrideFeature);
        features.Should().Contain(Constants.PortForwardingFeature);
    }

    // The reverse-forward (ssh -R) session-request authorization gate. ssh -L
    // rides a direct-tcpip channel the registered service authorizes on its own,
    // but tcpip-forward is a session request refused unless explicitly authorized
    // — and only while port forwarding is enabled, else an unregistered service
    // would leave a falsely acknowledged forward with no listener behind it.

    [Theory]
    [InlineData("tcpip-forward")]
    [InlineData("cancel-tcpip-forward")]
    public void ShouldAuthorizeSessionRequest_Enabled_AuthorizesReverseForward(string requestType)
    {
        SshServerService.ShouldAuthorizeSessionRequest(portForwardingEnabled: true, requestType)
            .Should().BeTrue();
    }

    [Theory]
    [InlineData("tcpip-forward")]
    [InlineData("cancel-tcpip-forward")]
    public void ShouldAuthorizeSessionRequest_Disabled_RefusesReverseForward(string requestType)
    {
        SshServerService.ShouldAuthorizeSessionRequest(portForwardingEnabled: false, requestType)
            .Should().BeFalse();
    }

    [Theory]
    [InlineData("env")]
    [InlineData("keepalive@openssh.com")]
    [InlineData("")]
    [InlineData(null)]
    public void ShouldAuthorizeSessionRequest_UnrelatedRequest_IsNotAuthorizedEvenWhenEnabled(string? requestType)
    {
        SshServerService.ShouldAuthorizeSessionRequest(portForwardingEnabled: true, requestType)
            .Should().BeFalse();
    }

    // The theories above feed the literal wire-protocol strings, so a typo that
    // drifted Constants.TcpipForwardRequestType away from "tcpip-forward" would
    // already fail them — no separate constant-value assertion is needed.

    // ---- ApplySessionRequestPolicy: the production handler's authorize + pin ----
    // These run the real policy against a real PortForwardingService, so deleting
    // the loopback pin or flipping it to fail-open (authorize without pinning)
    // turns one of them red — which a test over the pure decision helper cannot.

    [Fact]
    public void ApplySessionRequestPolicy_TcpipForward_GatewayOff_PinsLoopbackAndAuthorizes()
    {
        var (args, pfs) = MakeReverseForwardRequest(Constants.TcpipForwardRequestType);
        pfs.TcpListenerFactory.Should().NotBeSameAs(LoopbackTcpListenerFactory.Instance,
            "the service starts with the library's default factory");

        SshServerService.ApplySessionRequestPolicy(
            args, portForwardingEnabled: true, gatewayPortsEnabled: false, () => pfs, NullLogger.Instance);

        args.IsAuthorized.Should().BeTrue();
        pfs.TcpListenerFactory.Should().BeSameAs(LoopbackTcpListenerFactory.Instance);
    }

    [Fact]
    public void ApplySessionRequestPolicy_TcpipForward_GatewayOff_UnresolvableService_FailsClosed()
    {
        var (args, _) = MakeReverseForwardRequest(Constants.TcpipForwardRequestType);

        SshServerService.ApplySessionRequestPolicy(
            args, portForwardingEnabled: true, gatewayPortsEnabled: false, () => null, NullLogger.Instance);

        args.IsAuthorized.Should().BeFalse(
            "a reverse forward must be refused rather than left authorized-but-unpinned (which binds all interfaces)");
    }

    [Fact]
    public void ApplySessionRequestPolicy_TcpipForward_GatewayOn_AuthorizesAndHonorsClientBind()
    {
        var (args, pfs) = MakeReverseForwardRequest(Constants.TcpipForwardRequestType);
        var defaultFactory = pfs.TcpListenerFactory;

        // Must not even resolve the service: GatewayPorts on means no loopback pin.
        SshServerService.ApplySessionRequestPolicy(
            args, portForwardingEnabled: true, gatewayPortsEnabled: true,
            () => throw new InvalidOperationException("must not resolve when GatewayPorts is on"),
            NullLogger.Instance);

        args.IsAuthorized.Should().BeTrue();
        pfs.TcpListenerFactory.Should().BeSameAs(defaultFactory);
        pfs.TcpListenerFactory.Should().NotBeSameAs(LoopbackTcpListenerFactory.Instance);
    }

    [Fact]
    public void ApplySessionRequestPolicy_CancelTcpipForward_GatewayOff_AuthorizesWithoutPinning()
    {
        var (args, pfs) = MakeReverseForwardRequest(Constants.CancelTcpipForwardRequestType);
        var defaultFactory = pfs.TcpListenerFactory;

        SshServerService.ApplySessionRequestPolicy(
            args, portForwardingEnabled: true, gatewayPortsEnabled: false,
            () => throw new InvalidOperationException("cancel has no listener to pin"),
            NullLogger.Instance);

        args.IsAuthorized.Should().BeTrue();
        pfs.TcpListenerFactory.Should().BeSameAs(defaultFactory);
    }

    [Fact]
    public void ApplySessionRequestPolicy_PortForwardingOff_TcpipForward_IsRefused()
    {
        var (args, _) = MakeReverseForwardRequest(Constants.TcpipForwardRequestType);

        SshServerService.ApplySessionRequestPolicy(
            args, portForwardingEnabled: false, gatewayPortsEnabled: false,
            () => throw new InvalidOperationException("must not resolve when port forwarding is off"),
            NullLogger.Instance);

        args.IsAuthorized.Should().BeFalse();
    }

    private static (SshRequestEventArgs<SessionRequestMessage> args, PortForwardingService pfs)
        MakeReverseForwardRequest(string requestType)
    {
        // A non-connected session is enough to construct the service and read /
        // write its TcpListenerFactory; the policy never touches the wire.
        var session = new SshServerSession(new SshSessionConfiguration(), new TraceSource("test"));
        var pfs = new PortForwardingService(session);
        var args = new SshRequestEventArgs<SessionRequestMessage>(
            requestType, new SessionRequestMessage(), new ClaimsPrincipal(), CancellationToken.None);
        return (args, pfs);
    }

    private sealed class StubShellSelector : IShellSelector
    {
        public Task<ShellSelection> SelectAsync(ShellOverride sshOverride, CancellationToken cancellation)
            => Task.FromResult(new ShellSelection("/bin/sh", string.Empty));
    }
}
