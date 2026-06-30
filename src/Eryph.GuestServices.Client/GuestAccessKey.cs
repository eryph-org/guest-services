using System.Xml;
using Eryph.ComputeClient.Models;

namespace Eryph.GuestServices.Client;

// Authorizes a public key in a catlet guest via eryph (the runtime key-push
// flow). Decoupled from any console: returns the absolute expiry the server
// applies (or null when no ttl is given) and throws GuestConnectionException
// with a user-facing message on failure.
public static class GuestAccessKey
{
    public static async Task<DateTimeOffset?> AddAsync(
        EryphConnection connection,
        string catletId,
        string publicKey,
        TimeSpan? ttl = null,
        CancellationToken cancellation = default)
    {
        // ISO 8601 duration (informational) plus the absolute expiry the server applies.
        string? ttlIso = null;
        DateTimeOffset? expiresAt = null;
        if (ttl is { } t)
        {
            ttlIso = XmlConvert.ToString(t);
            expiresAt = DateTimeOffset.UtcNow.Add(t);
        }

        var body = new AddAccessKeyRequestBody(publicKey)
        {
            Ttl = ttlIso,
            ExpiresAt = expiresAt,
        };

        try
        {
            await connection.CreateCatletsClient(EryphConnection.RemoteAccessScope)
                .AddAccessKeyAsync(catletId, body, cancellation);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new GuestConnectionException($"Failed to add the key: {ex.Message}");
        }

        return expiresAt;
    }
}
