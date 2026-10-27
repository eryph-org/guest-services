using AwesomeAssertions;
using Eryph.GuestServices.Core;
using Eryph.GuestServices.DevTunnels.Ssh.Extensions;
using Eryph.GuestServices.Service.Services;
using Microsoft.DevTunnels.Ssh.Tcp;

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

    private sealed class StubShellSelector : IShellSelector
    {
        public Task<ShellSelection> SelectAsync(ShellOverride sshOverride, CancellationToken cancellation)
            => Task.FromResult(new ShellSelection("/bin/sh", string.Empty));
    }
}
