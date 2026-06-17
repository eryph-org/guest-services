using System.Reflection;
using Org.BouncyCastle.Bcpg.OpenPgp;

namespace Eryph.GuestServices.Provisioning.Update;

/// <summary>
/// Verifies a detached OpenPGP signature against the bundled dbosoft release
/// signing keys. The signed artifact is the release <c>SHA256SUMS</c> file;
/// trusting it (and therefore the per-file hashes it lists) reduces to trusting
/// these embedded public keys — so a tampered index or package is rejected even
/// though the index itself is only fetched over HTTPS.
/// </summary>
public interface IReleaseSignatureVerifier
{
    /// <summary>
    /// True when <paramref name="detachedSignature"/> is a valid signature over
    /// <paramref name="signedData"/> by one of the bundled dbosoft keys.
    /// </summary>
    bool Verify(byte[] signedData, byte[] detachedSignature);
}

/// <summary>
/// Default <see cref="IReleaseSignatureVerifier"/> backed by BouncyCastle and
/// the <c>dbosoft-release-keys.asc</c> embedded resource (both published
/// dbosoft signing keys, so a signature from either verifies).
/// </summary>
public sealed class ReleaseSignatureVerifier : IReleaseSignatureVerifier
{
    private const string KeyResourceSuffix = "dbosoft-release-keys.asc";

    private readonly Lazy<PgpPublicKeyRingBundle> keyring = new(LoadKeyring);

    public bool Verify(byte[] signedData, byte[] detachedSignature)
    {
        try
        {
            var signature = ReadSignature(detachedSignature);
            if (signature is null)
                return false;

            var key = keyring.Value.GetPublicKey(signature.KeyId);
            if (key is null)
                return false; // signed by a key we don't trust

            signature.InitVerify(key);
            signature.Update(signedData);
            return signature.Verify();
        }
        catch (Exception)
        {
            // The signature bytes are attacker-controlled; malformed input must
            // never throw out of here — an unparseable/garbage signature is
            // simply "not valid" (the caller fails closed on false).
            return false;
        }
    }

    private static PgpSignature? ReadSignature(byte[] detachedSignature)
    {
        using var input = PgpUtilities.GetDecoderStream(new MemoryStream(detachedSignature));
        var factory = new PgpObjectFactory(input);
        var obj = factory.NextPgpObject();

        // Detached sigs are normally a bare PgpSignatureList; tolerate a
        // compressed wrapper just in case.
        if (obj is PgpCompressedData compressed)
        {
            using var decompressed = compressed.GetDataStream();
            factory = new PgpObjectFactory(decompressed);
            obj = factory.NextPgpObject();
        }

        return obj is PgpSignatureList { Count: > 0 } list ? list[0] : null;
    }

    private static PgpPublicKeyRingBundle LoadKeyring()
    {
        var assembly = typeof(ReleaseSignatureVerifier).Assembly;
        var name = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(KeyResourceSuffix, StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                $"Embedded signing-key resource '{KeyResourceSuffix}' was not found.");

        using var stream = assembly.GetManifestResourceStream(name)!;
        using var decoder = PgpUtilities.GetDecoderStream(stream);
        return new PgpPublicKeyRingBundle(decoder);
    }
}
