using Microsoft.DevTunnels.Ssh;
using Microsoft.DevTunnels.Ssh.Keys;

namespace Eryph.GuestServices.Tool.Eryph;

// Resolves the public key for the key-push flows. Source precedence mirrors the
// RFC: an explicit --public-key path, stdin when that value is "-", or the
// managed ClientKeyHelper key when the option is omitted.
public static class PublicKeyReader
{
    public static async Task<string?> ResolveAsync(string? publicKeyOption)
    {
        if (publicKeyOption == "-")
        {
            try
            {
                using var reader = new StreamReader(Console.OpenStandardInput());
                var fromStdin = (await reader.ReadToEndAsync()).Trim();
                return string.IsNullOrEmpty(fromStdin) ? null : fromStdin;
            }
            catch (IOException)
            {
                // stdin broke; treat as "could not read" so the caller emits its
                // friendly message instead of a stack trace.
                return null;
            }
        }

        if (!string.IsNullOrEmpty(publicKeyOption))
        {
            if (!File.Exists(publicKeyOption))
                return null;

            try
            {
                return (await File.ReadAllTextAsync(publicKeyOption)).Trim();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // ACL/locked/unreadable file: same "could not read" outcome.
                return null;
            }
        }

        // No option: fall back to the managed client key.
        var keyPair = await ClientKeyHelper.GetKeyPairAsync();
        if (keyPair is null)
            return null;

        return KeyPair.ExportPublicKey(keyPair, keyFormat: KeyFormat.Ssh).Trim();
    }
}
