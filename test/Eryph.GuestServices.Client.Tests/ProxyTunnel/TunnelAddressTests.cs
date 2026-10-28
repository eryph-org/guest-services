using System.Net;
using AwesomeAssertions;
using Eryph.GuestServices.Client.ProxyTunnel;

namespace Eryph.GuestServices.Client.Tests.ProxyTunnel;

public class TunnelAddressTests
{
    [Theory]
    [InlineData("wass11.corp.example:3300", "wass11.corp.example", 3300)]
    [InlineData("127.0.0.1:8080", "127.0.0.1", 8080)]
    [InlineData("[::1]:8080", "::1", 8080)]
    public void TryParseTarget_accepts_single_host_port(string value, string host, int port)
    {
        TunnelAddress.TryParseTarget(value, out var h, out var p, out var error)
            .Should().BeTrue(error);
        h.Should().Be(host);
        p.Should().Be(port);
    }

    [Theory]
    [InlineData("*.corp.example:3300")]   // wildcard host
    [InlineData("host:3300,host2:3300")]  // list
    [InlineData("host:3300-3400")]        // port range
    [InlineData("host")]                  // no port
    [InlineData(":3300")]                 // no host
    [InlineData("host:0")]                // port out of range
    [InlineData("host:70000")]            // port out of range
    [InlineData("0.0.0.0:3300")]          // bind-any is not a single target
    [InlineData("fe80::1:3300")]          // unbracketed IPv6 is ambiguous
    [InlineData("")]
    [InlineData(null)]
    public void TryParseTarget_rejects_non_single_targets(string? value)
    {
        TunnelAddress.TryParseTarget(value, out _, out _, out var error).Should().BeFalse();
        error.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void TryParseListen_bare_port_binds_loopback()
    {
        TunnelAddress.TryParseListen("8080", out var ep, out var error).Should().BeTrue(error);
        ep.Address.Should().Be(IPAddress.Loopback);
        ep.Port.Should().Be(8080);
    }

    [Theory]
    [InlineData("127.0.0.1:8080", "127.0.0.1", 8080)]
    [InlineData("0.0.0.0:8080", "0.0.0.0", 8080)]  // explicit non-loopback bind is opt-in
    [InlineData("[::1]:8080", "::1", 8080)]
    public void TryParseListen_honors_explicit_ip(string value, string ip, int port)
    {
        TunnelAddress.TryParseListen(value, out var ep, out var error).Should().BeTrue(error);
        ep.Address.Should().Be(IPAddress.Parse(ip));
        ep.Port.Should().Be(port);
    }

    [Theory]
    [InlineData("example.com:8080")]  // hostname is not a literal bind address
    [InlineData("*:8080")]
    [InlineData("8080,9090")]
    [InlineData("127.0.0.1:0")]
    [InlineData("")]
    [InlineData(null)]
    public void TryParseListen_rejects_bad_binds(string? value)
    {
        TunnelAddress.TryParseListen(value, out _, out var error).Should().BeFalse();
        error.Should().NotBeNullOrWhiteSpace();
    }
}
