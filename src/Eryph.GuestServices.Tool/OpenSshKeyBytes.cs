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
        // Scan first and only allocate when there is actually a CRLF to strip. An
        // already-LF key (the common case, including a second pass over an
        // already-normalized key) is handed back untouched with no copy.
        var firstCrlf = IndexOfCrlf(bytes, 0);
        if (firstCrlf < 0)
            return bytes;

        var result = new byte[bytes.Length - 1];
        Array.Copy(bytes, 0, result, 0, firstCrlf);
        var length = firstCrlf;
        for (var i = firstCrlf; i < bytes.Length; i++)
        {
            // Drop only a CR that directly precedes an LF; a lone CR or any high
            // (0x80+) byte is preserved so the key material is byte-exact.
            if (bytes[i] == 0x0D && i + 1 < bytes.Length && bytes[i + 1] == 0x0A)
                continue;

            result[length++] = bytes[i];
        }

        return length == result.Length ? result : result[..length];
    }

    private static int IndexOfCrlf(byte[] bytes, int start)
    {
        for (var i = start; i + 1 < bytes.Length; i++)
        {
            if (bytes[i] == 0x0D && bytes[i + 1] == 0x0A)
                return i;
        }

        return -1;
    }
}
