using System.Text;
using AwesomeAssertions;
using Eryph.GuestServices.Tool;
using Microsoft.DevTunnels.Ssh;
using Microsoft.DevTunnels.Ssh.Algorithms;
using Microsoft.DevTunnels.Ssh.Keys;

namespace Eryph.GuestServices.Tool.Tests;

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
    public void NormalizeLineEndingsToLf_RealDevTunnelsKey_HasNoCrlf_AndStillImports()
    {
        // Tie the regression to the real producer: DevTunnels exports the
        // OpenSSH key with CRLF (KeyData.EncodePem -> Environment.NewLine), which
        // MSYS/MINGW libcrypto rejects. After normalization no CR remains and the
        // key still parses, proving the material is untouched.
        var keyPair = SshAlgorithms.PublicKey.ECDsaSha2Nistp256.GenerateKeyPair();
        var exported = KeyPair.ExportPrivateKeyBytes(keyPair, keyFormat: KeyFormat.OpenSsh);
        exported.Should().Contain((byte)0x0D, "DevTunnels exports CRLF on Windows (precondition for this test)");

        var normalized = OpenSshKeyBytes.NormalizeLineEndingsToLf(exported);

        normalized.Should().NotContain((byte)0x0D);
        var reimported = KeyPair.ImportKeyBytes(normalized);
        KeyPair.ExportPublicKeyBytes(reimported, keyFormat: KeyFormat.Ssh)
            .Should().Equal(KeyPair.ExportPublicKeyBytes(keyPair, keyFormat: KeyFormat.Ssh));
    }
}
