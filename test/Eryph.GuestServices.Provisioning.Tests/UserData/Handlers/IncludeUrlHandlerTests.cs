using System.Text;
using AwesomeAssertions;
using Eryph.GuestServices.Provisioning.UserData;
using Eryph.GuestServices.Provisioning.UserData.Handlers;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Eryph.GuestServices.Provisioning.Tests.UserData.Handlers;

public sealed class IncludeUrlHandlerTests
{
    [Fact]
    public async Task ProcessAsync_FetchesEachUrlAndDispatchesNested()
    {
        var urlHelper = Substitute.For<IUrlHelper>();
        urlHelper.FetchAsync("https://a/cfg.yaml", Arg.Any<CancellationToken>())
            .Returns(Encoding.UTF8.GetBytes("#cloud-config\nhostname: alpha\n"));
        urlHelper.FetchAsync("https://b/script.ps1", Arg.Any<CancellationToken>())
            .Returns(Encoding.UTF8.GetBytes("#ps1\nWrite-Host hello\n"));

        var handler = new IncludeUrlHandler(urlHelper, NullLogger<IncludeUrlHandler>.Instance);
        var includeBody = Encoding.UTF8.GetBytes(
            "#include\n# a comment\nhttps://a/cfg.yaml\nhttps://b/script.ps1\n");
        var part = new UserDataPart("text/x-include-url", includeBody, null);
        var ctx = new TestResolutionContext();

        await handler.ProcessAsync(part, ctx, CancellationToken.None);

        ctx.NestedParts.Should().HaveCount(2);
        ctx.NestedParts[0].ContentType.Should().Be("text/x-cloud-config");
        ctx.NestedParts[1].ContentType.Should().Be("text/x-shellscript");
    }

    [Fact]
    public async Task ProcessAsync_SkipsAlreadyVisitedUrl()
    {
        var urlHelper = Substitute.For<IUrlHelper>();
        var handler = new IncludeUrlHandler(urlHelper, NullLogger<IncludeUrlHandler>.Instance);
        var ctx = new TestResolutionContext();
        ctx.TryMarkVisited("https://x").Should().BeTrue();

        var part = new UserDataPart(
            "text/x-include-url",
            Encoding.UTF8.GetBytes("#include\nhttps://x\n"),
            null);

        await handler.ProcessAsync(part, ctx, CancellationToken.None);

        await urlHelper.DidNotReceiveWithAnyArgs().FetchAsync(default!, default);
        ctx.NestedParts.Should().BeEmpty();
    }

    [Fact]
    public async Task ProcessAsync_TolerantOfFetchFailures()
    {
        var urlHelper = Substitute.For<IUrlHelper>();
        urlHelper.FetchAsync("https://broken", Arg.Any<CancellationToken>())
            .Throws(new HttpRequestException("offline"));
        urlHelper.FetchAsync("https://ok/cfg.yaml", Arg.Any<CancellationToken>())
            .Returns(Encoding.UTF8.GetBytes("#cloud-config\nhostname: ok\n"));

        var handler = new IncludeUrlHandler(urlHelper, NullLogger<IncludeUrlHandler>.Instance);
        var part = new UserDataPart(
            "text/x-include-url",
            Encoding.UTF8.GetBytes("#include\nhttps://broken\nhttps://ok/cfg.yaml\n"),
            null);
        var ctx = new TestResolutionContext();

        await handler.ProcessAsync(part, ctx, CancellationToken.None);

        ctx.NestedParts.Should().ContainSingle()
            .Which.ContentType.Should().Be("text/x-cloud-config");
    }

    [Fact]
    public async Task ProcessAsync_TolerantOfSingleLineIncludeFormat()
    {
        // Some real-world producers (notably hand-edited Azure CustomData)
        // put the URL on the marker line itself: `#include https://...`.
        // Cloud-init docs say the URL belongs on a separate line, but the
        // single-line shape is common enough that silently dropping it
        // (because the line starts with `#`) felt like a bug to operators.
        // We accept both shapes; sniffer still routes the part here.
        var urlHelper = Substitute.For<IUrlHelper>();
        urlHelper.FetchAsync("https://a/cfg.yaml", Arg.Any<CancellationToken>())
            .Returns(Encoding.UTF8.GetBytes("#cloud-config\nhostname: alpha\n"));

        var handler = new IncludeUrlHandler(urlHelper, NullLogger<IncludeUrlHandler>.Instance);
        var part = new UserDataPart(
            "text/x-include-url",
            Encoding.UTF8.GetBytes("#include https://a/cfg.yaml\n"),
            null);
        var ctx = new TestResolutionContext();

        await handler.ProcessAsync(part, ctx, CancellationToken.None);

        await urlHelper.Received(1).FetchAsync("https://a/cfg.yaml", Arg.Any<CancellationToken>());
        ctx.NestedParts.Should().ContainSingle()
            .Which.ContentType.Should().Be("text/x-cloud-config");
    }

    [Fact]
    public async Task ProcessAsync_TolerantOfSingleLineIncludeOnceFormat()
    {
        // Same tolerance applies to #include-once. Marker is matched
        // longest-first so `-once` wins; the URL after the whitespace
        // remains intact.
        var urlHelper = Substitute.For<IUrlHelper>();
        urlHelper.FetchAsync("https://b/cfg.yaml", Arg.Any<CancellationToken>())
            .Returns(Encoding.UTF8.GetBytes("#cloud-config\nhostname: bravo\n"));

        var handler = new IncludeUrlHandler(urlHelper, NullLogger<IncludeUrlHandler>.Instance);
        var part = new UserDataPart(
            "text/x-include-once-url",
            Encoding.UTF8.GetBytes("#include-once https://b/cfg.yaml\n"),
            null);
        var ctx = new TestResolutionContext();

        await handler.ProcessAsync(part, ctx, CancellationToken.None);

        await urlHelper.Received(1).FetchAsync("https://b/cfg.yaml", Arg.Any<CancellationToken>());
        ctx.NestedParts.Should().HaveCount(1);
    }

    [Fact]
    public async Task ProcessAsync_AcceptsInlineUrlOnMarkerLineThenAdditionalUrlsBelow()
    {
        // Mixed shape: marker carries one URL inline, followed by a
        // standard multi-line body. Both URLs must be fetched.
        var urlHelper = Substitute.For<IUrlHelper>();
        urlHelper.FetchAsync("https://a/cfg.yaml", Arg.Any<CancellationToken>())
            .Returns(Encoding.UTF8.GetBytes("#cloud-config\nhostname: alpha\n"));
        urlHelper.FetchAsync("https://b/cfg.yaml", Arg.Any<CancellationToken>())
            .Returns(Encoding.UTF8.GetBytes("#cloud-config\nhostname: bravo\n"));

        var handler = new IncludeUrlHandler(urlHelper, NullLogger<IncludeUrlHandler>.Instance);
        var part = new UserDataPart(
            "text/x-include-url",
            Encoding.UTF8.GetBytes("#include https://a/cfg.yaml\n# a comment\nhttps://b/cfg.yaml\n"),
            null);
        var ctx = new TestResolutionContext();

        await handler.ProcessAsync(part, ctx, CancellationToken.None);

        await urlHelper.Received(1).FetchAsync("https://a/cfg.yaml", Arg.Any<CancellationToken>());
        await urlHelper.Received(1).FetchAsync("https://b/cfg.yaml", Arg.Any<CancellationToken>());
        ctx.NestedParts.Should().HaveCount(2);
    }

    [Fact]
    public async Task ProcessAsync_StripsMarkerLineWhenStandardMultiLineFormatIsUsed()
    {
        // Regression for the original behaviour: marker on its own line +
        // URLs below. Must still work — the new tolerance must not break
        // the documented shape. (The pre-existing
        // ProcessAsync_FetchesEachUrlAndDispatchesNested covers this too,
        // but the contract is worth pinning here next to the tolerance
        // cases so the relationship is obvious.)
        var urlHelper = Substitute.For<IUrlHelper>();
        urlHelper.FetchAsync("https://a/cfg.yaml", Arg.Any<CancellationToken>())
            .Returns(Encoding.UTF8.GetBytes("#cloud-config\nhostname: alpha\n"));

        var handler = new IncludeUrlHandler(urlHelper, NullLogger<IncludeUrlHandler>.Instance);
        var part = new UserDataPart(
            "text/x-include-url",
            Encoding.UTF8.GetBytes("#include\nhttps://a/cfg.yaml\n"),
            null);
        var ctx = new TestResolutionContext();

        await handler.ProcessAsync(part, ctx, CancellationToken.None);

        await urlHelper.Received(1).FetchAsync("https://a/cfg.yaml", Arg.Any<CancellationToken>());
        ctx.NestedParts.Should().ContainSingle();
    }

    [Fact]
    public async Task ProcessAsync_HandlesMarkerPrefixOnAnyLine_MatchingCloudInit()
    {
        // Cloud-init's cloudinit/user_data.py:_do_include strips the marker
        // prefix from EVERY line that has it, not just the first — so a
        // payload with two `#include URL` lines yields two URLs. Pins the
        // alignment with cloud-init after we removed the `seenMarker` flag
        // that previously made line 2+ misbehave.
        var urlHelper = Substitute.For<IUrlHelper>();
        urlHelper.FetchAsync("https://a/cfg.yaml", Arg.Any<CancellationToken>())
            .Returns(Encoding.UTF8.GetBytes("#cloud-config\nhostname: alpha\n"));
        urlHelper.FetchAsync("https://b/cfg.yaml", Arg.Any<CancellationToken>())
            .Returns(Encoding.UTF8.GetBytes("#cloud-config\nhostname: bravo\n"));

        var handler = new IncludeUrlHandler(urlHelper, NullLogger<IncludeUrlHandler>.Instance);
        var part = new UserDataPart(
            "text/x-include-url",
            Encoding.UTF8.GetBytes("#include https://a/cfg.yaml\n#include https://b/cfg.yaml\n"),
            null);
        var ctx = new TestResolutionContext();

        await handler.ProcessAsync(part, ctx, CancellationToken.None);

        await urlHelper.Received(1).FetchAsync("https://a/cfg.yaml", Arg.Any<CancellationToken>());
        await urlHelper.Received(1).FetchAsync("https://b/cfg.yaml", Arg.Any<CancellationToken>());
        ctx.NestedParts.Should().HaveCount(2);
    }

    [Fact]
    public async Task ProcessAsync_HandlesUtf8BomFromWindowsPowerShellSetContent()
    {
        // Companion to the single-line tolerance fix: with a UTF-8 BOM
        // prefix (PowerShell Set-Content -Encoding UTF8), the marker line
        // is "﻿#include https://..." — does NOT start with "#include"
        // because the first character is U+FEFF. Without explicit BOM
        // stripping the inline URL is silently dropped (this was the
        // exact symptom: "#include URL (single line) is filtered as
        // comment" when authoring catlet user-data on Windows).
        var urlHelper = Substitute.For<IUrlHelper>();
        urlHelper.FetchAsync("https://a/cfg.yaml", Arg.Any<CancellationToken>())
            .Returns(Encoding.UTF8.GetBytes("#cloud-config\nhostname: bom-include\n"));

        var handler = new IncludeUrlHandler(urlHelper, NullLogger<IncludeUrlHandler>.Instance);
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }
            .Concat(Encoding.UTF8.GetBytes("#include https://a/cfg.yaml\n"))
            .ToArray();
        var part = new UserDataPart("text/x-include-url", bytes, null);
        var ctx = new TestResolutionContext();

        await handler.ProcessAsync(part, ctx, CancellationToken.None);

        await urlHelper.Received(1).FetchAsync("https://a/cfg.yaml", Arg.Any<CancellationToken>());
        ctx.NestedParts.Should().ContainSingle();
    }

    [Fact]
    public async Task ProcessAsync_DecompressesGzippedPayload()
    {
        var urlHelper = Substitute.For<IUrlHelper>();
        urlHelper.FetchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Gzip(Encoding.UTF8.GetBytes("#cloud-config\nhostname: zipped\n")));

        var handler = new IncludeUrlHandler(urlHelper, NullLogger<IncludeUrlHandler>.Instance);
        var part = new UserDataPart(
            "text/x-include-url",
            Encoding.UTF8.GetBytes("#include\nhttps://gz\n"),
            null);
        var ctx = new TestResolutionContext();

        await handler.ProcessAsync(part, ctx, CancellationToken.None);

        ctx.NestedParts.Should().ContainSingle()
            .Which.ContentType.Should().Be("text/x-cloud-config");
    }

    private static byte[] Gzip(byte[] data)
    {
        using var output = new MemoryStream();
        using (var gz = new System.IO.Compression.GZipStream(output, System.IO.Compression.CompressionMode.Compress))
        {
            gz.Write(data, 0, data.Length);
        }
        return output.ToArray();
    }
}
