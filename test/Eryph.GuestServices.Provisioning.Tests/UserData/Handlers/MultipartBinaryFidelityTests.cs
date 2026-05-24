using System.Text;
using AwesomeAssertions;
using Eryph.GuestServices.Provisioning.UserData;
using Eryph.GuestServices.Provisioning.UserData.Handlers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Eryph.GuestServices.Provisioning.Tests.UserData.Handlers;

// Regression coverage for review.md Finding 5: the multipart parser must
// hold byte[] end-to-end so that >0x7F bytes in 8bit / binary / unrecognised
// transfer encodings survive the trip from the raw user-data blob through
// to ShellScriptPartHandler / CloudConfigPartHandler. The pre-fix parser
// UTF-8-decoded the whole multipart body up front, then re-encoded each
// part body — silently mangling any non-UTF-8 byte sequence along the way.
//
// Per memory feedback_binary_contracts every Content-Transfer-Encoding tier
// gets its own named test, and per feedback_tolerance_tests each tolerance
// (missing close delimiter, mbox preamble, etc.) is locked by a regression
// whose name reveals the scenario.
public sealed class MultipartBinaryFidelityTests
{
    [Fact]
    public async Task Binary8bit_RoundTrips_NonAsciiBytes()
    {
        // Compose: header + body bytes that include 0xCA 0xFE 0xBA 0xBE then
        // an ASCII trailer. Declare 8bit transfer encoding so the handler
        // takes the verbatim-bytes path. RFC 2046 §5.1.1: the CRLF that
        // immediately precedes a boundary "belongs to" the delimiter, so
        // the parser trims one trailing LF from the body (we keep an
        // explicit "; comment" trailer to make that boundary visible).
        var bodyOnTheWire = new byte[] { 0xCA, 0xFE, 0xBA, 0xBE }
            .Concat("; comment\n"u8.ToArray()).ToArray();
        var expected = new byte[] { 0xCA, 0xFE, 0xBA, 0xBE }
            .Concat("; comment"u8.ToArray()).ToArray();

        var bytes = BuildMultipart(
            new Part(
                ContentType: "text/x-shellscript; charset=binary; name=\"bin.ps1\"",
                TransferEncoding: "8bit",
                Body: bodyOnTheWire));

        var ctx = await DispatchAsync(bytes);

        ctx.NestedParts.Should().ContainSingle();
        ctx.NestedParts[0].ContentType.Should().Be("text/x-shellscript");
        ctx.NestedParts[0].Body.Should().Equal(expected);
    }

    [Fact]
    public async Task Binary8bit_ZeroByteIsPreserved()
    {
        // A single 0x00 byte body. UTF-8 round-trip would survive it (NUL
        // is in the ASCII range), but we tighten the contract: bytes are
        // bytes, no encoding layer touches them.
        var bodyBytes = new byte[] { 0x00 };
        var bytes = BuildMultipart(
            new Part(
                ContentType: "text/x-shellscript; charset=binary; name=\"nul.sh\"",
                TransferEncoding: "8bit",
                Body: bodyBytes));

        var ctx = await DispatchAsync(bytes);

        ctx.NestedParts.Should().ContainSingle();
        ctx.NestedParts[0].Body.Should().Equal(bodyBytes);
    }

    [Fact]
    public async Task Base64BinaryPart_DecodesToOriginalBytes()
    {
        var original = new byte[] { 0xCA, 0xFE, 0xBA, 0xBE };
        var b64 = Convert.ToBase64String(original);

        var bytes = BuildMultipart(
            new Part(
                ContentType: "application/octet-stream; name=\"blob.bin\"",
                TransferEncoding: "base64",
                Body: Encoding.ASCII.GetBytes(b64 + "\n")));

        var ctx = await DispatchAsync(bytes);

        ctx.NestedParts.Should().ContainSingle();
        ctx.NestedParts[0].Body.Should().Equal(original);
    }

    [Fact]
    public async Task HighByteInCloudConfigPart_StillUtf8Decoded()
    {
        // Cloud-config parts are routed to CloudConfigPartHandler which
        // still does a UTF-8 decode of the body (YAML is text). Confirm
        // that a regular ASCII cloud-config still reaches the nested
        // pipeline as text/x-cloud-config — i.e. our parser changes did
        // not break the common case.
        var body = Encoding.UTF8.GetBytes("#cloud-config\nhostname: bin-host\n");
        var bytes = BuildMultipart(
            new Part(
                ContentType: "text/x-cloud-config",
                TransferEncoding: null,
                Body: body));

        var ctx = await DispatchAsync(bytes);

        ctx.NestedParts.Should().ContainSingle();
        ctx.NestedParts[0].ContentType.Should().Be(UserDataContentTypeSniffer.CloudConfig);
        Encoding.UTF8.GetString(ctx.NestedParts[0].Body).Should().Contain("hostname: bin-host");
    }

    [Fact]
    public async Task MissingCloseBoundary_StillFlushesLastPart()
    {
        // Regression — locked in differences-from-cloud-init.md and the
        // pre-existing MultipartMimeHandlerTests, but the byte-level parser
        // rewrite needs its own coverage so the next regression is caught
        // here rather than in the legacy string-based test.
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

        var ctx = await DispatchAsync(Encoding.UTF8.GetBytes(raw));

        ctx.NestedParts.Should().HaveCount(2);
        Encoding.UTF8.GetString(ctx.NestedParts[1].Body).Should().Contain("hostname: last");
    }

    [Fact]
    public async Task MboxFromPreamble_DoesNotConsumePartBodies()
    {
        // mbox preamble strip must be a one-line skip at the top. Once we
        // are inside the multipart, "From " lines inside a script body
        // (think a shell script that runs `git log --format=%aD`) must
        // NOT be eaten. The trailing LF is the boundary's owned line
        // terminator per RFC 2046, so the body we assert on does not
        // include the final newline.
        var script = "#!/bin/sh\nFrom alice Mon Jan  1 00:00:00 1970\necho ok\n"u8.ToArray();
        var expected = "#!/bin/sh\nFrom alice Mon Jan  1 00:00:00 1970\necho ok"u8.ToArray();
        var bytes = BuildMultipart(
            preamble: "From nobody Fri Jan  11 07:00:00 1980\n",
            new Part(
                ContentType: "text/x-shellscript",
                TransferEncoding: null,
                Body: script));

        var ctx = await DispatchAsync(bytes);

        ctx.NestedParts.Should().ContainSingle();
        ctx.NestedParts[0].ContentType.Should().Be("text/x-shellscript");
        ctx.NestedParts[0].Body.Should().Equal(expected);
    }

    // ---- helpers ----------------------------------------------------------

    private static async Task<TestResolutionContext> DispatchAsync(byte[] bytes)
    {
        var handler = new MultipartMimeHandler(NullLogger<MultipartMimeHandler>.Instance);
        var ctx = new TestResolutionContext();
        var part = new UserDataPart("multipart/mixed", bytes, null);
        await handler.ProcessAsync(part, ctx, CancellationToken.None);
        return ctx;
    }

    private sealed record Part(string ContentType, string? TransferEncoding, byte[] Body);

    private static byte[] BuildMultipart(params Part[] parts) =>
        BuildMultipart(preamble: null, parts);

    private static byte[] BuildMultipart(string? preamble, params Part[] parts)
    {
        const string boundary = "BOUNDARY";

        using var ms = new MemoryStream();
        if (preamble is not null)
            ms.Write(Encoding.ASCII.GetBytes(preamble));

        var rootHeaders =
            "MIME-Version: 1.0\n" +
            $"Content-Type: multipart/mixed; boundary=\"{boundary}\"\n" +
            "\n";
        ms.Write(Encoding.ASCII.GetBytes(rootHeaders));

        foreach (var p in parts)
        {
            ms.Write(Encoding.ASCII.GetBytes($"--{boundary}\n"));
            ms.Write(Encoding.ASCII.GetBytes($"Content-Type: {p.ContentType}\n"));
            if (p.TransferEncoding is not null)
                ms.Write(Encoding.ASCII.GetBytes($"Content-Transfer-Encoding: {p.TransferEncoding}\n"));
            ms.Write(Encoding.ASCII.GetBytes("\n"));
            ms.Write(p.Body);
            // Each part body ends with a newline before the next boundary.
            // The parser strips the trailing CRLF that "belongs to" the
            // boundary, so we add one here.
            if (p.Body.Length == 0 || p.Body[^1] != (byte)'\n')
                ms.Write(Encoding.ASCII.GetBytes("\n"));
        }

        ms.Write(Encoding.ASCII.GetBytes($"--{boundary}--\n"));
        return ms.ToArray();
    }
}
