using AwesomeAssertions;
using Microsoft.DevTunnels.Ssh;
using Microsoft.DevTunnels.Ssh.Algorithms;
using NSubstitute;

namespace Eryph.GuestServices.Client.Tests;

// Guards the contract an embedding application (e.g. an eryph app) relies on:
// the client's public operations are interfaces, so the app can substitute them
// in its own unit tests and drive the guest-services flow without a real eryph
// or catlet. If any of these seams became a sealed concrete, these tests stop
// compiling — which is the point.
public class ClientMockabilityTests
{
    [Fact]
    public async Task IEryphGuestServicesClient_AddAccessKey_can_be_substituted_and_verified()
    {
        var expiry = DateTimeOffset.Parse("2031-01-01T00:00:00Z");
        var client = Substitute.For<IEryphGuestServicesClient>();
        client.AddAccessKeyAsync("catlet-1", "ssh-ed25519 AAAA", Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<DateTimeOffset?>(expiry));

        var result = await client.AddAccessKeyAsync("catlet-1", "ssh-ed25519 AAAA", TimeSpan.FromHours(8));

        result.Should().Be(expiry);
        await client.Received(1).AddAccessKeyAsync(
            "catlet-1", "ssh-ed25519 AAAA", TimeSpan.FromHours(8), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IEryphGuestServicesClient_Connect_can_be_substituted_to_fail()
    {
        var client = Substitute.For<IEryphGuestServicesClient>();
        var key = SshAlgorithms.PublicKey.ECDsaSha2Nistp256.GenerateKeyPair();
        client.ConnectAsync("catlet-1", key, Arg.Any<Action<string>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<GuestSshConnection>(new GuestConnectionException("no route to guest")));

        var act = () => client.ConnectAsync("catlet-1", key);

        await act.Should().ThrowAsync<GuestConnectionException>().WithMessage("no route to guest");
    }

    [Fact]
    public async Task IGuestConnector_transport_seam_can_be_substituted()
    {
        // The lower-level transport abstraction both connectors implement — mocked
        // for the host-agent (Hyper-V) case where there is no EryphConnection.
        var connector = Substitute.For<IGuestConnector>();
        connector.ConnectAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<GuestSshConnection>(new GuestConnectionException("socket closed")));

        var act = () => connector.ConnectAsync(CancellationToken.None);

        await act.Should().ThrowAsync<GuestConnectionException>().WithMessage("socket closed");
    }
}
