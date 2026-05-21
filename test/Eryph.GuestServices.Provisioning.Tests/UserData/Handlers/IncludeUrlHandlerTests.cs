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
