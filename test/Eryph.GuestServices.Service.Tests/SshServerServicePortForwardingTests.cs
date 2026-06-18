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

    private sealed class StubShellSelector : IShellSelector
    {
        public Task<ShellSelection> SelectAsync(ShellOverride sshOverride, CancellationToken cancellation)
            => Task.FromResult(new ShellSelection("/bin/sh", string.Empty));
    }
}
