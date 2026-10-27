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
/// interface.
/// </summary>
public class LoopbackTcpListenerFactoryTests
{
    [Theory]
    [InlineData("0.0.0.0")]   // IPAddress.Any — what a bare `ssh -R <port>` resolves to
    [InlineData("127.0.0.1")] // already loopback
    [InlineData("10.1.2.3")]  // a concrete non-loopback address
    public async Task CreateTcpListenerAsync_BindsLoopback_RegardlessOfRequestedAddress(string requested)
    {
        var requestedAddress = IPAddress.Parse(requested);

        // localPort 0 lets the OS choose a free port, so the test never collides.
        var listener = await LoopbackTcpListenerFactory.Instance.CreateTcpListenerAsync(
            remotePort: null,
            localIPAddress: requestedAddress,
            localPort: 0,
            canChangeLocalPort: true,
            trace: new TraceSource(nameof(LoopbackTcpListenerFactoryTests)),
            cancellation: CancellationToken.None);

        try
        {
            var endpoint = (IPEndPoint)listener.LocalEndpoint;
            endpoint.Address.Should().Be(IPAddress.Loopback);
            IPAddress.IsLoopback(endpoint.Address).Should().BeTrue();
        }
        finally
        {
            listener.Stop();
        }
    }
}
