using Microsoft.DevTunnels.Ssh.Algorithms;

namespace Eryph.GuestServices.Client;

// The default IEryphGuestServicesClient: drives the eryph remote channel for a
// resolved connection. Thin by design — it composes EryphGuestConnector and
// GuestAccessKey so consumers depend on (and mock) one interface instead of the
// individual primitives.
public sealed class EryphGuestServicesClient(EryphConnection connection) : IEryphGuestServicesClient
{
    public Task<GuestSshConnection> ConnectAsync(
        string catletId,
        IKeyPair keyPair,
        Action<string>? writeWarning = null,
        CancellationToken cancellation = default) =>
        new EryphGuestConnector(connection, catletId, keyPair, writeWarning)
            .ConnectAsync(cancellation);

    public Task<DateTimeOffset?> AddAccessKeyAsync(
        string catletId,
        string publicKey,
        TimeSpan? ttl = null,
        CancellationToken cancellation = default) =>
        GuestAccessKey.AddAsync(connection, catletId, publicKey, ttl, cancellation);
}
