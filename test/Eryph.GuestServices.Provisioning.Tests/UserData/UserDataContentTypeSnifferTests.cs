using System.Text;
using AwesomeAssertions;
using Eryph.GuestServices.Provisioning.UserData;

namespace Eryph.GuestServices.Provisioning.Tests.UserData;

public sealed class UserDataContentTypeSnifferTests
{
    [Fact]
    public void Sniff_CloudConfigMarker_Returns_CloudConfig()
    {
        var body = Encoding.UTF8.GetBytes("#cloud-config\nusers: []");
        UserDataContentTypeSniffer.Sniff(body).Should().Be(UserDataContentTypeSniffer.CloudConfig);
    }

    // Regression: Pester's invalid-yaml.yaml was written by PowerShell's
    // Set-Content which emits a UTF-8 BOM. The sniffer must strip the BOM
    // before reading the leading marker line.
    [Fact]
    public void Sniff_CloudConfigMarkerWithUtf8Bom_Returns_CloudConfig()
    {
        var withBom = new List<byte> { 0xEF, 0xBB, 0xBF };
        withBom.AddRange(Encoding.UTF8.GetBytes("#cloud-config\nusers: []"));
        UserDataContentTypeSniffer.Sniff(withBom.ToArray()).Should().Be(UserDataContentTypeSniffer.CloudConfig);
    }

    [Fact]
    public void Sniff_ShellScriptShebangWithUtf8Bom_Returns_ShellScript()
    {
        var withBom = new List<byte> { 0xEF, 0xBB, 0xBF };
        withBom.AddRange(Encoding.UTF8.GetBytes("#ps1_sysnative\nWrite-Host hi"));
        UserDataContentTypeSniffer.Sniff(withBom.ToArray()).Should().Be(UserDataContentTypeSniffer.ShellScript);
    }

    [Fact]
    public void Sniff_NoMarker_Returns_PlainText()
    {
        var body = Encoding.UTF8.GetBytes("just some text\nwith no marker");
        UserDataContentTypeSniffer.Sniff(body).Should().Be(UserDataContentTypeSniffer.PlainText);
    }

    [Fact]
    public void Sniff_IncludeMarker_Returns_IncludeUrl()
    {
        var body = Encoding.UTF8.GetBytes("#include\nhttps://example.com/userdata");
        UserDataContentTypeSniffer.Sniff(body).Should().Be(UserDataContentTypeSniffer.IncludeUrl);
    }

    [Fact]
    public void Sniff_MultipartHeader_Returns_MultipartMixed()
    {
        var body = Encoding.UTF8.GetBytes("Content-Type: multipart/mixed; boundary=foo\n\n--foo\n");
        UserDataContentTypeSniffer.Sniff(body).Should().Be(UserDataContentTypeSniffer.MultipartMixed);
    }

    // Regression: eryph-zero's configdrive user-data (gzipped, then this MIME
    // message inside) is prefixed with an mbox-style "From " line per RFC 4155.
    // Cloud-init handles it; we didn't, so the sniffer returned PlainText and
    // the whole user-data was silently dropped on real catlets. The unit suite
    // tested the "Content-Type at first line" path but not the realistic
    // mbox-preamble path.
    [Fact]
    public void Sniff_MboxFromPreambleBeforeMultipartHeader_Returns_MultipartMixed()
    {
        var body = Encoding.UTF8.GetBytes(
            "From nobody Fri Jan  11 07:00:00 1980\n" +
            "Content-Type: multipart/mixed; boundary=\"==BOUNDARY==\"\n" +
            "MIME-Version: 1.0\n\n" +
            "--==BOUNDARY==\n");
        UserDataContentTypeSniffer.Sniff(body).Should().Be(UserDataContentTypeSniffer.MultipartMixed);
    }

    [Fact]
    public void Sniff_GzippedMboxPreambleMultipart_DecompressFirst_ReturnsMultipart()
    {
        // Real eryph user-data: gzip(mbox-preamble + multipart MIME). Callers
        // are expected to DecompressIfGzipped before Sniff, which is what
        // UserDataPipeline does.
        var inner = Encoding.UTF8.GetBytes(
            "From nobody Fri Jan  11 07:00:00 1980\n" +
            "Content-Type: multipart/mixed; boundary=\"==B==\"\n" +
            "MIME-Version: 1.0\n\n" +
            "--==B==\nContent-Type: text/x-cloud-config\n\n#cloud-config\nhostname: x\n");
        using var ms = new MemoryStream();
        using (var gz = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionMode.Compress, leaveOpen: true))
        {
            gz.Write(inner, 0, inner.Length);
        }
        var compressed = ms.ToArray();
        UserDataContentTypeSniffer.IsGzipped(compressed).Should().BeTrue();
        var decompressed = UserDataContentTypeSniffer.DecompressIfGzipped(compressed);
        UserDataContentTypeSniffer.Sniff(decompressed).Should().Be(UserDataContentTypeSniffer.MultipartMixed);
    }
}
