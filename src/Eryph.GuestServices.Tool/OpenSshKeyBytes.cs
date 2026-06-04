namespace Eryph.GuestServices.Tool;

internal static class OpenSshKeyBytes
{
    // Microsoft.DevTunnels.Ssh writes its PEM/SSH key text with
    // StringBuilder.AppendLine (KeyData.EncodePem), whose terminator is
    // Environment.NewLine — so on Windows the exported OpenSSH key carries CRLF
    // line endings. Native Windows OpenSSH tolerates that, but the MSYS/MINGW
    // libcrypto used by Git-Bash's ssh rejects a CR inside the base64 body
    // ("error in libcrypto") and refuses to load the key. OpenSSH keys are
    // canonically LF, so normalize CRLF -> LF before writing them to disk.
    //
    // Works on the raw bytes (no round-trip through string) so the exact key
    // material is preserved: only a CR that directly precedes an LF is dropped;
    // every other byte — a lone CR or any high (0x80+) byte — is left untouched.
    public static byte[] NormalizeLineEndingsToLf(byte[] bytes)
    {
        var result = new byte[bytes.Length];
        var length = 0;
        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] == 0x0D && i + 1 < bytes.Length && bytes[i + 1] == 0x0A)
                continue;

            result[length++] = bytes[i];
        }

        // No CRLF found: hand back the original bytes untouched (the common case
        // for an already-LF key), otherwise the trimmed copy.
        return length == bytes.Length ? bytes : result[..length];
    }
}
