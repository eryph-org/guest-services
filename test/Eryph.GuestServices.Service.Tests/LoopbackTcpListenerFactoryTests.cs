using System.Diagnostics;
using System.Net;
using AwesomeAssertions;
using Eryph.GuestServices.Service.Services;

namespace Eryph.GuestServices.Service.Tests;

/// <summary>
/// Pins the loopback-only reverse-forward bind posture (OpenSSH
/// <c>GatewayPorts no</c>): the factory must bind loopback even when the caller
/// asks for a non-loopback address, because a bare <c>ssh -R 8080:…</c> arrives
/// as <see cref="IPAddress.Any"/> and would otherwise expose the guest on every
/// interface. It must, however, honor the requested address <em>family</em> — the
/// library binds twice (IPv4 then IPv6) on the same port for a loopback/any
/// request, so a family-blind factory would fail the second bind and refuse the
/// whole forward.
/// </summary>
public class LoopbackTcpListenerFactoryTests
{
    private static readonly TraceSource Trace = new(nameof(LoopbackTcpListenerFactoryTests));

    [Theory]
    [InlineData("0.0.0.0", "127.0.0.1")]   // IPAddress.Any — what a bare `ssh -R <port>` resolves to
    [InlineData("127.0.0.1", "127.0.0.1")] // already loopback
    [InlineData("10.1.2.3", "127.0.0.1")]  // a concrete non-loopback IPv4 address
    [InlineData("::", "::1")]              // IPAddress.IPv6Any
    [InlineData("::1", "::1")]             // already IPv6 loopback
    [InlineData("2001:db8::1", "::1")]     // a concrete non-loopback IPv6 address
    public async Task CreateTcpListenerAsync_BindsLoopbackOfRequestedFamily(string requested, string expected)
    {
        var listener = await LoopbackTcpListenerFactory.Instance.CreateTcpListenerAsync(
            remotePort: null,
            localIPAddress: IPAddress.Parse(requested),
            localPort: 0,
            canChangeLocalPort: true,
            trace: Trace,
            cancellation: CancellationToken.None);

        try
        {
            var endpoint = (IPEndPoint)listener.LocalEndpoint;
            endpoint.Address.Should().Be(IPAddress.Parse(expected));
            IPAddress.IsLoopback(endpoint.Address).Should().BeTrue();
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task CreateTcpListenerAsync_ModellingTheLibrarysDualBind_BothSucceedOnLoopback()
    {
        // Reproduces LocalPortForwarder.StartForwardingAsync for a loopback/any
        // request: bind IPv4 on an OS-chosen port, then bind IPv6 on the SAME
        // port with canChangeLocalPort:false. A family-blind factory would make
        // the second call throw AddressAlreadyInUse (both 127.0.0.1:<port>) and
        // the reverse forward would be refused — this is the regression guard.
        var v4 = await LoopbackTcpListenerFactory.Instance.CreateTcpListenerAsync(
            remotePort: null, localIPAddress: IPAddress.Any, localPort: 0,
            canChangeLocalPort: true, trace: Trace, cancellation: CancellationToken.None);
        try
        {
            var port = ((IPEndPoint)v4.LocalEndpoint).Port;

            var v6 = await LoopbackTcpListenerFactory.Instance.CreateTcpListenerAsync(
                remotePort: null, localIPAddress: IPAddress.IPv6Any, localPort: port,
                canChangeLocalPort: false, trace: Trace, cancellation: CancellationToken.None);
            try
            {
                ((IPEndPoint)v4.LocalEndpoint).Address.Should().Be(IPAddress.Loopback);
                ((IPEndPoint)v6.LocalEndpoint).Address.Should().Be(IPAddress.IPv6Loopback);
                ((IPEndPoint)v6.LocalEndpoint).Port.Should().Be(port);
            }
            finally
            {
                v6.Stop();
            }
        }
        finally
        {
            v4.Stop();
        }
    }
}
