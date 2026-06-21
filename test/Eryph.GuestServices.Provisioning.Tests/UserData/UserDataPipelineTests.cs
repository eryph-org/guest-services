using System.Text;
using AwesomeAssertions;
using Eryph.GuestServices.Provisioning.Serialization;
using Eryph.GuestServices.Provisioning.UserData;
using Eryph.GuestServices.Provisioning.UserData.Handlers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Eryph.GuestServices.Provisioning.Tests.UserData;

public sealed class UserDataPipelineTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(),
        "egs-pipeline-" + Guid.NewGuid().ToString("N"));

    public UserDataPipelineTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task ResolveAsync_Null_ReturnsEmpty()
    {
        var pipeline = BuildPipeline(out _);
        var result = await pipeline.ResolveAsync(null, CancellationToken.None);

        result.CloudConfig.Hostname.Should().BeNull();
        result.Scripts.Should().BeEmpty();
        result.Boothooks.Should().BeEmpty();
    }

    [Fact]
    public async Task ResolveAsync_PlainCloudConfig_ParsesAndReturns()
    {
        var pipeline = BuildPipeline(out _);
        var raw = Encoding.UTF8.GetBytes("#cloud-config\nhostname: plain\n");

        var result = await pipeline.ResolveAsync(raw, CancellationToken.None);

        result.CloudConfig.Hostname.Should().Be("plain");
    }

    [Fact]
    public async Task ResolveAsync_RawShellScript_CapturesScript()
    {
        var pipeline = BuildPipeline(out _);
        var raw = Encoding.UTF8.GetBytes("#ps1\nWrite-Host hi\n");

        var result = await pipeline.ResolveAsync(raw, CancellationToken.None);

        result.Scripts.Should().ContainSingle();
        result.Scripts[0].Kind.Should().Be(ScriptKind.PowerShell);
    }

    [Fact]
    public async Task ResolveAsync_RawBoothook_CapturesBoothook()
    {
        var pipeline = BuildPipeline(out _);
        var raw = Encoding.UTF8.GetBytes("#cloud-boothook\necho hi\n");

        var result = await pipeline.ResolveAsync(raw, CancellationToken.None);

        result.Boothooks.Should().ContainSingle();
    }

    [Fact]
    public async Task ResolveAsync_MultipartWithCloudConfigAndScript_BothLand()
    {
        // Filename-led detection (RFC 0007): the .ps1 extension wins over body
        // shape, so a text/x-shellscript part with filename="setup.ps1" maps
        // to ScriptKind.PowerShell on Windows — matching cbi's dispatch.
        var pipeline = BuildPipeline(out _);
        const string raw =
            "MIME-Version: 1.0\r\n" +
            "Content-Type: multipart/mixed; boundary=\"B\"\r\n" +
            "\r\n" +
            "--B\r\n" +
            "Content-Type: text/x-cloud-config\r\n" +
            "\r\n" +
            "#cloud-config\nhostname: mp\n" +
            "\r\n" +
            "--B\r\n" +
            "Content-Type: text/x-shellscript\r\n" +
            "Content-Disposition: attachment; filename=\"setup.ps1\"\r\n" +
            "\r\n" +
            "Write-Host mp\n" +
            "\r\n" +
            "--B--\r\n";

        var result = await pipeline.ResolveAsync(Encoding.UTF8.GetBytes(raw), CancellationToken.None);

        result.CloudConfig.Hostname.Should().Be("mp");
        result.Scripts.Should().ContainSingle()
            .Which.Kind.Should().Be(ScriptKind.PowerShell);
    }

    [Fact]
    public async Task ResolveAsync_IncludeUrlFromFile_FetchesAndMergesCloudConfig()
    {
        var includedPath = Path.Combine(_tempDir, "included.yaml");
        await File.WriteAllTextAsync(includedPath, "#cloud-config\nhostname: included\n");
        var fileUrl = new Uri(includedPath).AbsoluteUri;

        var pipeline = BuildPipeline(out _);
        var raw = Encoding.UTF8.GetBytes($"#include\n{fileUrl}\n");

        var result = await pipeline.ResolveAsync(raw, CancellationToken.None);

        result.CloudConfig.Hostname.Should().Be("included");
    }

    [Fact]
    public async Task ResolveAsync_IncludeUrlReturningAnotherInclude_RecursesAndMerges()
    {
        var leaf = Path.Combine(_tempDir, "leaf.yaml");
        await File.WriteAllTextAsync(leaf, "#cloud-config\nhostname: nested\n");

        var middle = Path.Combine(_tempDir, "middle.txt");
        await File.WriteAllTextAsync(middle, $"#include\n{new Uri(leaf).AbsoluteUri}\n");

        var pipeline = BuildPipeline(out _);
        var raw = Encoding.UTF8.GetBytes($"#include\n{new Uri(middle).AbsoluteUri}\n");

        var result = await pipeline.ResolveAsync(raw, CancellationToken.None);

        result.CloudConfig.Hostname.Should().Be("nested");
    }

    [Fact]
    public async Task ResolveAsync_TwoCloudConfigFragments_MergeRightOverridesLeft()
    {
        // Build a multipart with two cloud-config parts; the second hostname wins.
        const string raw =
            "Content-Type: multipart/mixed; boundary=\"B\"\r\n" +
            "\r\n" +
            "--B\r\n" +
            "Content-Type: text/x-cloud-config\r\n" +
            "\r\n" +
            "#cloud-config\nhostname: first\nruncmd:\n  - echo first\n" +
            "\r\n" +
            "--B\r\n" +
            "Content-Type: text/x-cloud-config\r\n" +
            "\r\n" +
            "#cloud-config\nhostname: second\nruncmd:\n  - echo second\n" +
            "\r\n" +
            "--B--\r\n";

        var pipeline = BuildPipeline(out _);
        var result = await pipeline.ResolveAsync(Encoding.UTF8.GetBytes(raw), CancellationToken.None);

        result.CloudConfig.Hostname.Should().Be("second");
        result.CloudConfig.Runcmd.Should().NotBeNull().And.HaveCount(2);
    }

    [Fact]
    public async Task ResolveAsync_SecondFragmentMergeHowReplace_ReplacesAccumulatedList()
    {
        // The second cloud-config part carries merge_how: list(replace) (RFC 0032),
        // so its runcmd replaces the first part's rather than appending.
        const string raw =
            "Content-Type: multipart/mixed; boundary=\"B\"\r\n" +
            "\r\n" +
            "--B\r\n" +
            "Content-Type: text/x-cloud-config\r\n" +
            "\r\n" +
            "#cloud-config\nruncmd:\n  - echo first\n" +
            "\r\n" +
            "--B\r\n" +
            "Content-Type: text/x-cloud-config\r\n" +
            "\r\n" +
            "#cloud-config\nmerge_how: list(replace)\nruncmd:\n  - echo second\n" +
            "\r\n" +
            "--B--\r\n";

        var pipeline = BuildPipeline(out _);
        var result = await pipeline.ResolveAsync(Encoding.UTF8.GetBytes(raw), CancellationToken.None);

        result.CloudConfig.Runcmd.Should().ContainSingle();
        result.CloudConfig.Runcmd![0].Command.Should().Be("echo second");
    }

    [Fact]
    public async Task ResolveAsync_UnrecognisedRoot_ReturnsEmptyAndWarns()
    {
        var pipeline = BuildPipeline(out _);
        var raw = Encoding.UTF8.GetBytes("just some plain text without a marker\n");

        var result = await pipeline.ResolveAsync(raw, CancellationToken.None);

        result.CloudConfig.Hostname.Should().BeNull();
        result.Scripts.Should().BeEmpty();
    }

    private UserDataPipeline BuildPipeline(out IUrlHelper urlHelper)
    {
        urlHelper = new UrlHelper(NullLogger<UrlHelper>.Instance);
        var serializer = new CloudConfigSerializer(NullLogger<CloudConfigSerializer>.Instance);
        var handlers = new IUserDataHandler[]
        {
            new MultipartMimeHandler(NullLogger<MultipartMimeHandler>.Instance),
            new IncludeUrlHandler(urlHelper, NullLogger<IncludeUrlHandler>.Instance),
            new CloudConfigPartHandler(serializer, NullLogger<CloudConfigPartHandler>.Instance),
            new ShellScriptPartHandler(NullLogger<ShellScriptPartHandler>.Instance),
            new BoothookPartHandler(NullLogger<BoothookPartHandler>.Instance),
        };
        return new UserDataPipeline(handlers, NullLogger<UserDataPipeline>.Instance);
    }
}
