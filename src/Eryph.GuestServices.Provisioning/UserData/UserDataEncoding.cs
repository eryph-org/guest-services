using System.Text;

namespace Eryph.GuestServices.Provisioning.UserData;

/// <summary>
/// UTF-8 decoding helper for user-data parts. PowerShell's
/// <c>Set-Content -Encoding UTF8</c> writes a BOM-prefixed payload; if the
/// BOM survives into the per-handler decode, YamlDotNet rejects the
/// cloud-config (U+FEFF before <c>#cloud-config</c>), the include parser
/// fails to recognise the marker (line no longer starts with
/// <c>#include</c>), and the MIME parser misreads the first header line.
/// Stripping the BOM here keeps every downstream parser on the same byte
/// alignment as cloud-init's PyYAML, which ignores a leading BOM.
/// </summary>
internal static class UserDataEncoding
{
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    /// <summary>Decode bytes as UTF-8, skipping a leading UTF-8 BOM if present.</summary>
    public static string DecodeUtf8(byte[]? body)
    {
        if (body is null || body.Length == 0)
            return string.Empty;

        var offset = body.Length >= Utf8Bom.Length
                     && body[0] == Utf8Bom[0]
                     && body[1] == Utf8Bom[1]
                     && body[2] == Utf8Bom[2]
            ? Utf8Bom.Length
            : 0;

        return Encoding.UTF8.GetString(body, offset, body.Length - offset);
    }
}
