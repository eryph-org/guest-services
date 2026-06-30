using Microsoft.DevTunnels.Ssh.Algorithms;

namespace Eryph.GuestServices.Client;

// The eryph (off-host) guest-services operations a consumer performs against a
// catlet over the eryph remote channel: open an authenticated SSH session, and
// authorize an access key. This is the seam an embedding application (e.g. an
// eryph app) depends on and substitutes in its own unit tests — mock this
// interface to drive the guest-services flow without a real eryph or catlet.
//
// The host-local Hyper-V transport is reached through HyperVGuestConnector, which
// implements IGuestConnector — mock that interface for the host-agent case.
public interface IEryphGuestServicesClient
{
    // Opens an authenticated in-process SSH session to the catlet guest. The
    // caller owns the returned connection and disposes it. Throws
    // GuestConnectionException when the session cannot be established.
    Task<GuestSshConnection> ConnectAsync(
        string catletId,
        IKeyPair keyPair,
        Action<string>? writeWarning = null,
        CancellationToken cancellation = default);

    // Authorizes a public key in the catlet guest. Returns the absolute expiry
    // when a ttl is given, else null. Throws GuestConnectionException on failure.
    Task<DateTimeOffset?> AddAccessKeyAsync(
        string catletId,
        string publicKey,
        TimeSpan? ttl = null,
        CancellationToken cancellation = default);
}
