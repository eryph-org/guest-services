using System.Text;
using AwesomeAssertions;
using Eryph.GuestServices.Provisioning.UserData;
using Eryph.GuestServices.Provisioning.UserData.Handlers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Eryph.GuestServices.Provisioning.Tests.UserData.Handlers;

public sealed class MultipartMimeHandlerTests
{
    [Fact]
    public async Task ProcessAsync_DispatchesEachChildPart()
    {
        const string raw =
            "MIME-Version: 1.0\r\n" +
            "Content-Type: multipart/mixed; boundary=\"BOUNDARY\"\r\n" +
            "\r\n" +
            "--BOUNDARY\r\n" +
            "Content-Type: text/x-cloud-config\r\n" +
            "\r\n" +
            "#cloud-config\nhostname: multi\n" +
            "\r\n" +
            "--BOUNDARY\r\n" +
            "Content-Type: text/x-shellscript\r\n" +
            "Content-Disposition: attachment; filename=\"boot.ps1\"\r\n" +
            "\r\n" +
            "#ps1\nWrite-Host hi\n" +
            "\r\n" +
            "--BOUNDARY--\r\n";

        var handler = new MultipartMimeHandler(NullLogger<MultipartMimeHandler>.Instance);
        var part = new UserDataPart("multipart/mixed", Encoding.UTF8.GetBytes(raw), null);
        var ctx = new TestResolutionContext();

        await handler.ProcessAsync(part, ctx, CancellationToken.None);

        ctx.NestedParts.Should().HaveCount(2);
        ctx.NestedParts[0].ContentType.Should().Be("text/x-cloud-config");
        Encoding.UTF8.GetString(ctx.NestedParts[0].Body).Should().Contain("hostname: multi");
        ctx.NestedParts[1].ContentType.Should().Be("text/x-shellscript");
        ctx.NestedParts[1].Filename.Should().Be("boot.ps1");
    }

    [Fact]
    public async Task ProcessAsync_TreatsUnknownInnerTypeAsSniffed()
    {
        const string raw =
            "Content-Type: multipart/mixed; boundary=\"B\"\r\n" +
            "\r\n" +
            "--B\r\n" +
            "Content-Type: text/plain\r\n" +
            "\r\n" +
            "#cloud-config\nhostname: sniffed\n" +
            "\r\n" +
            "--B--\r\n";

        var handler = new MultipartMimeHandler(NullLogger<MultipartMimeHandler>.Instance);
        var ctx = new TestResolutionContext();
        var part = new UserDataPart("multipart/mixed", Encoding.UTF8.GetBytes(raw), null);

        await handler.ProcessAsync(part, ctx, CancellationToken.None);

        ctx.NestedParts.Should().ContainSingle()
            .Which.ContentType.Should().Be("text/x-cloud-config");
    }
}
