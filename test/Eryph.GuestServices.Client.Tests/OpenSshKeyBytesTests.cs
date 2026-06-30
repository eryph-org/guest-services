using System.Text;
using AwesomeAssertions;
using Microsoft.DevTunnels.Ssh;
using Microsoft.DevTunnels.Ssh.Algorithms;
using Microsoft.DevTunnels.Ssh.Keys;

namespace Eryph.GuestServices.Client.Tests;

public class OpenSshKeyBytesTests
{
    [Fact]
    public void NormalizeLineEndingsToLf_ConvertsCrlfToLf()
    {
        var input = Encoding.ASCII.GetBytes("-----BEGIN-----\r\nAAAA\r\nBBBB\r\n-----END-----\r\n");

        var result = OpenSshKeyBytes.NormalizeLineEndingsToLf(input);

        Encoding.ASCII.GetString(result)
            .Should().Be("-----BEGIN-----\nAAAA\nBBBB\n-----END-----\n");
    }

    [Fact]
    public void NormalizeLineEndingsToLf_AlreadyLf_IsUnchanged()
    {
        var input = Encoding.ASCII.GetBytes("line1\nline2\n");

        OpenSshKeyBytes.NormalizeLineEndingsToLf(input).Should().Equal(input);
    }

    [Fact]
    public void NormalizeLineEndingsToLf_PreservesHighBytesAndLoneCr_BinaryContract()
    {
        // Only a CR that directly precedes an LF is dropped. High (0x80+) bytes
        // must survive byte-for-byte, and a lone CR (not part of a CRLF) is kept
        // so the routine never corrupts non-text content.
        var input = new byte[] { 0x80, 0x0D, 0x0A, 0xFF, 0x0D, 0x80, 0x0A };

        var result = OpenSshKeyBytes.NormalizeLineEndingsToLf(input);

        // 0x80, [CR dropped] 0x0A, 0xFF, 0x0D (lone, kept), 0x80, 0x0A
        result.Should().Equal(0x80, 0x0A, 0xFF, 0x0D, 0x80, 0x0A);
    }

    [Fact]
    public void DevTunnels_ExportsOpenSshKeyWithCrlf_OnWindows_WhichIsWhyWeNormalize()
    {
        // Canary for the upstream quirk this whole helper exists for: on Windows,
        // DevTunnels' KeyData.EncodePem builds the PEM with Environment.NewLine, so
        // the exported key carries CRLF, which MSYS/MINGW libcrypto rejects. This
        // test deliberately documents that precondition: if it ever starts failing
        // (DevTunnels switched to LF, or this runs on a non-CRLF platform), the LF
        // normalization may have become a no-op worth revisiting — it is NOT a
        // failure of NormalizeLineEndingsToLf, which the test below verifies on its
        // own without depending on the producer's line endings.
        var keyPair = SshAlgorithms.PublicKey.ECDsaSha2Nistp256.GenerateKeyPair();
        var exported = KeyPair.ExportPrivateKeyBytes(keyPair, keyFormat: KeyFormat.OpenSsh);

        exported.Should().Contain((byte)0x0D);
    }

    [Fact]
    public void NormalizeLineEndingsToLf_RealDevTunnelsKey_StripsAllCr_AndStillImports()
    {
        // Functional regression against the real producer, robust to whatever line
        // endings it emits: export a genuine DevTunnels key, normalize it, and
        // assert no CR remains and the key still parses to the same material. No
        // precondition on CRLF presence — if the export is already LF, normalize
        // is a no-op and the import must still round-trip identically.
        var keyPair = SshAlgorithms.PublicKey.ECDsaSha2Nistp256.GenerateKeyPair();
        var exported = KeyPair.ExportPrivateKeyBytes(keyPair, keyFormat: KeyFormat.OpenSsh);

        var normalized = OpenSshKeyBytes.NormalizeLineEndingsToLf(exported);

        normalized.Should().NotContain((byte)0x0D);
        var reimported = KeyPair.ImportKeyBytes(normalized);
        KeyPair.ExportPublicKeyBytes(reimported, keyFormat: KeyFormat.Ssh)
            .Should().Equal(KeyPair.ExportPublicKeyBytes(keyPair, keyFormat: KeyFormat.Ssh));
    }
}
