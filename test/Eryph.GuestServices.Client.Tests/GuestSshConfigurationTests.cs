using AwesomeAssertions;
using Microsoft.DevTunnels.Ssh.Tcp;

namespace Eryph.GuestServices.Client.Tests;

// Pins the client session config the proxied tunnel depends on. Without
// PortForwardingService registered here, the engine's
// ActivateService<PortForwardingService>() throws KeyNotFoundException and the
// tunnel cannot start in production — a gap the in-memory engine tests (which
// share a PFS-enabled config across client and server) cannot catch.
public class GuestSshConfigurationTests
{
    [Fact]
    public void Registers_port_forwarding_service_only_when_enabled()
    {
        GuestSsh.BuildClientConfiguration(keepAliveTimeoutSeconds: 0, enablePortForwarding: true)
            .Services.Should().ContainKey(typeof(PortForwardingService));

        GuestSsh.BuildClientConfiguration(keepAliveTimeoutSeconds: 0, enablePortForwarding: false)
            .Services.Should().NotContainKey(typeof(PortForwardingService));
    }

    [Fact]
    public void Sets_keepalive_timeout_only_when_positive()
    {
        GuestSsh.BuildClientConfiguration(keepAliveTimeoutSeconds: 15, enablePortForwarding: false)
            .KeepAliveTimeoutInSeconds.Should().Be(15);

        // 0 leaves the library default untouched (shell/transfer callers unchanged).
        GuestSsh.BuildClientConfiguration(keepAliveTimeoutSeconds: 0, enablePortForwarding: false)
            .KeepAliveTimeoutInSeconds.Should().Be(0);
    }
}
