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
}
