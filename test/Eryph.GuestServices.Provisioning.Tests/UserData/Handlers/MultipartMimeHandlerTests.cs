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

    // eryph-zero's config drive is produced by Dbosoft.CloudInit.ConfigDrive's
    // UserDataSerializer, which never emits the RFC 2046 `--boundary--` close
    // delimiter — every part simply ends at EOF. cloud-init's email-based
    // parser tolerates this, so we must too: dropping the trailing part would
    // discard the entire cloud-config whenever the payload is a single part
    // (the common eryph case). This regression guards that behaviour — see
    // also ProcessAsync_ParsesEryphZeroConfigDriveShape below for the exact
    // producer framing.
    [Fact]
    public async Task ProcessAsync_FlushesTrailingPartWhenCloseDelimiterMissing()
    {
        // Note the absence of "--BOUNDARY--" terminator at the end.
        const string raw =
            "MIME-Version: 1.0\n" +
            "Content-Type: multipart/mixed; boundary=\"BOUNDARY\"\n" +
            "\n" +
            "--BOUNDARY\n" +
            "Content-Type: text/x-cloud-config\n" +
            "\n" +
            "#cloud-config\nhostname: first\n" +
            "--BOUNDARY\n" +
            "Content-Type: text/x-cloud-config\n" +
            "\n" +
            "#cloud-config\nhostname: last\n";

        var handler = new MultipartMimeHandler(NullLogger<MultipartMimeHandler>.Instance);
        var part = new UserDataPart("multipart/mixed", Encoding.UTF8.GetBytes(raw), null);
        var ctx = new TestResolutionContext();

        await handler.ProcessAsync(part, ctx, CancellationToken.None);

        ctx.NestedParts.Should().HaveCount(2, "the trailing part ends at EOF, not a dropped fragment");
        Encoding.UTF8.GetString(ctx.NestedParts[0].Body).Should().Contain("hostname: first");
        Encoding.UTF8.GetString(ctx.NestedParts[1].Body).Should().Contain("hostname: last");
    }

    // Real-world fixture: the exact byte framing emitted by
    // Dbosoft.CloudInit.ConfigDrive.UserDataSerializer (the producer eryph-zero
    // uses). Distinctive quirks, all of which the parser must absorb:
    //   - mbox "From nobody ..." envelope line (space, not colon) as preamble
    //   - boundary token "==BOUNDARY==" declared with quotes
    //   - LF-only line endings (no CR)
    //   - a single cloud-config part and NO "--==BOUNDARY==--" close delimiter
    // This is the shape that produced the empty-config e2e failure; the part
    // must be dispatched, not dropped.
    [Fact]
    public async Task ProcessAsync_ParsesEryphZeroConfigDriveShape()
    {
        const string raw =
            "From nobody Fri Jan  11 07:00:00 1980\n" +
            "Content-Type: multipart/mixed; boundary=\"==BOUNDARY==\"\n" +
            "MIME-Version: 1.0\n" +
            "--==BOUNDARY==\n" +
            "MIME-Version: 1.0\n" +
            "Content-Type: text/cloud-config; charset=\"us-ascii\"\n" +
            "Content-Disposition: attachment; filename=\"eryph-config\"\n" +
            "#cloud-config\nhostname: egs-prov-e2e\n";

        var handler = new MultipartMimeHandler(NullLogger<MultipartMimeHandler>.Instance);
        var part = new UserDataPart("multipart/mixed", Encoding.ASCII.GetBytes(raw), null);
        var ctx = new TestResolutionContext();

        await handler.ProcessAsync(part, ctx, CancellationToken.None);

        ctx.NestedParts.Should().ContainSingle();
        ctx.NestedParts[0].ContentType.Should().Be("text/x-cloud-config");
        ctx.NestedParts[0].Filename.Should().Be("eryph-config");
        Encoding.UTF8.GetString(ctx.NestedParts[0].Body).Should().Contain("hostname: egs-prov-e2e");
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

    [Fact]
    public async Task ProcessAsync_HandlesUtf8BomFromWindowsPowerShellSetContent()
    {
        // PowerShell-authored multipart payload prefixed by EF BB BF. A
        // raw Encoding.UTF8.GetString preserves the BOM as U+FEFF, so the
        // first "MIME-Version:" header no longer starts at column 0 and
        // the parser sees the document as headerless body. Decoding via
        // UserDataEncoding.DecodeUtf8 strips the BOM so headers parse.
        const string raw =
            "MIME-Version: 1.0\r\n" +
            "Content-Type: multipart/mixed; boundary=\"B\"\r\n" +
            "\r\n" +
            "--B\r\n" +
            "Content-Type: text/x-cloud-config\r\n" +
            "\r\n" +
            "#cloud-config\nhostname: bom-mime\n" +
            "\r\n" +
            "--B--\r\n";
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }
            .Concat(Encoding.UTF8.GetBytes(raw))
            .ToArray();

        var handler = new MultipartMimeHandler(NullLogger<MultipartMimeHandler>.Instance);
        var ctx = new TestResolutionContext();
        var part = new UserDataPart("multipart/mixed", bytes, null);

        await handler.ProcessAsync(part, ctx, CancellationToken.None);

        ctx.NestedParts.Should().ContainSingle()
            .Which.ContentType.Should().Be("text/x-cloud-config");
    }

    // cbi-compat: cloudbase-init encodes the filename in the Content-Type
    // `name=` parameter (in addition to / instead of Content-Disposition).
    // Our parser extracts it from Content-Type when Content-Disposition is absent.
    [Fact]
    public async Task ProcessAsync_ExtractsFilenameFromContentTypeName()
    {
        const string raw =
            "Content-Type: multipart/mixed; boundary=\"B\"\r\n" +
            "\r\n" +
            "--B\r\n" +
            "Content-Type: text/x-shellscript; charset=us-ascii; name=\"setup.ps1\"\r\n" +
            "\r\n" +
            "Write-Host cbi-compat\n" +
            "\r\n" +
            "--B--\r\n";

        var handler = new MultipartMimeHandler(NullLogger<MultipartMimeHandler>.Instance);
        var ctx = new TestResolutionContext();
        var part = new UserDataPart("multipart/mixed", Encoding.UTF8.GetBytes(raw), null);

        await handler.ProcessAsync(part, ctx, CancellationToken.None);

        ctx.NestedParts.Should().ContainSingle()
            .Which.Filename.Should().Be("setup.ps1");
    }
}
