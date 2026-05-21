using System.IO.Compression;
using System.Text;
using AwesomeAssertions;
using Eryph.GuestServices.CloudConfig;
using Eryph.GuestServices.Provisioning.Handlers;
using Eryph.GuestServices.Provisioning.Stages;
using Eryph.GuestServices.Provisioning.Windows;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using CloudConfigModel = global::Eryph.GuestServices.CloudConfig.CloudConfig;

namespace Eryph.GuestServices.Provisioning.Tests.Handlers;

public sealed class WriteFilesHandlerTests
{
    [Fact]
    public async Task Writes_plain_text_content()
    {
        var os = Substitute.For<IWindowsOs>();
        os.TranslateUnixPath("/etc/foo").Returns(@"C:\etc\foo");

        var handler = new WriteFilesHandler(NullLogger<WriteFilesHandler>.Instance);
        var config = new CloudConfigModel
        {
            WriteFiles = [new WriteFileConfig { Path = "/etc/foo", Content = "hello" }],
        };

        var result = await handler.ApplyAsync(config, new TestHandlerContext(os), CancellationToken.None);

        result.Should().BeOfType<HandlerOutcome.Completed>();
        await os.Received().EnsureDirectoryAsync(@"C:\etc", Arg.Any<CancellationToken>());
        await os.Received().WriteFileAsync(
            @"C:\etc\foo",
            Arg.Is<byte[]>(b => Encoding.UTF8.GetString(b) == "hello"),
            false,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Decodes_base64_content()
    {
        var os = Substitute.For<IWindowsOs>();
        os.TranslateUnixPath("/etc/foo").Returns(@"C:\etc\foo");

        var b64 = Convert.ToBase64String("hello"u8.ToArray());
        var handler = new WriteFilesHandler(NullLogger<WriteFilesHandler>.Instance);
        var config = new CloudConfigModel
        {
            WriteFiles = [new WriteFileConfig { Path = "/etc/foo", Content = b64, Encoding = "b64" }],
        };

        await handler.ApplyAsync(config, new TestHandlerContext(os), CancellationToken.None);

        await os.Received().WriteFileAsync(
            @"C:\etc\foo",
            Arg.Is<byte[]>(b => Encoding.UTF8.GetString(b) == "hello"),
            false,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Decodes_gzip_plus_base64_content()
    {
        var os = Substitute.For<IWindowsOs>();
        os.TranslateUnixPath("/etc/foo").Returns(@"C:\etc\foo");

        var raw = "hello world"u8.ToArray();
        using var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            gzip.Write(raw, 0, raw.Length);
        var encoded = Convert.ToBase64String(compressed.ToArray());

        var handler = new WriteFilesHandler(NullLogger<WriteFilesHandler>.Instance);
        var config = new CloudConfigModel
        {
            WriteFiles = [new WriteFileConfig { Path = "/etc/foo", Content = encoded, Encoding = "gz+b64" }],
        };

        await handler.ApplyAsync(config, new TestHandlerContext(os), CancellationToken.None);

        await os.Received().WriteFileAsync(
            @"C:\etc\foo",
            Arg.Is<byte[]>(b => Encoding.UTF8.GetString(b) == "hello world"),
            false,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Honors_append_flag()
    {
        var os = Substitute.For<IWindowsOs>();
        os.TranslateUnixPath("/etc/foo").Returns(@"C:\etc\foo");

        var handler = new WriteFilesHandler(NullLogger<WriteFilesHandler>.Instance);
        var config = new CloudConfigModel
        {
            WriteFiles = [new WriteFileConfig { Path = "/etc/foo", Content = "x", Append = true }],
        };

        await handler.ApplyAsync(config, new TestHandlerContext(os), CancellationToken.None);

        await os.Received().WriteFileAsync(
            @"C:\etc\foo", Arg.Any<byte[]>(), true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Applies_owner_when_set()
    {
        var os = Substitute.For<IWindowsOs>();
        os.TranslateUnixPath("/etc/foo").Returns(@"C:\etc\foo");

        var handler = new WriteFilesHandler(NullLogger<WriteFilesHandler>.Instance);
        var config = new CloudConfigModel
        {
            WriteFiles = [new WriteFileConfig { Path = "/etc/foo", Content = "x", Owner = "alice:devs" }],
        };

        await handler.ApplyAsync(config, new TestHandlerContext(os), CancellationToken.None);

        await os.Received().SetFileOwnerAsync(@"C:\etc\foo", "alice:devs", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Does_not_call_set_owner_when_owner_absent()
    {
        var os = Substitute.For<IWindowsOs>();
        os.TranslateUnixPath("/etc/foo").Returns(@"C:\etc\foo");

        var handler = new WriteFilesHandler(NullLogger<WriteFilesHandler>.Instance);
        var config = new CloudConfigModel
        {
            WriteFiles = [new WriteFileConfig { Path = "/etc/foo", Content = "x" }],
        };

        await handler.ApplyAsync(config, new TestHandlerContext(os), CancellationToken.None);

        await os.DidNotReceive().SetFileOwnerAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Fails_when_path_translation_throws_for_traversal()
    {
        var os = Substitute.For<IWindowsOs>();
        os.TranslateUnixPath("/../../Windows/notepad.exe")
            .Returns(_ => throw new ArgumentException("path contains '..'"));

        var handler = new WriteFilesHandler(NullLogger<WriteFilesHandler>.Instance);
        var config = new CloudConfigModel
        {
            WriteFiles = [new WriteFileConfig { Path = "/../../Windows/notepad.exe", Content = "evil" }],
        };

        var result = await handler.ApplyAsync(config, new TestHandlerContext(os), CancellationToken.None);

        result.Should().BeOfType<HandlerOutcome.Failed>()
            .Which.Reason.Should().Contain("path traversal");
        await os.DidNotReceive().WriteFileAsync(
            Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        await os.DidNotReceive().EnsureDirectoryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Skips_entries_with_unsupported_encoding()
    {
        var os = Substitute.For<IWindowsOs>();
        os.TranslateUnixPath("/etc/foo").Returns(@"C:\etc\foo");

        var handler = new WriteFilesHandler(NullLogger<WriteFilesHandler>.Instance);
        var config = new CloudConfigModel
        {
            WriteFiles = [new WriteFileConfig { Path = "/etc/foo", Content = "x", Encoding = "rot13" }],
        };

        var result = await handler.ApplyAsync(config, new TestHandlerContext(os), CancellationToken.None);

        result.Should().BeOfType<HandlerOutcome.Completed>();
        await os.DidNotReceive().WriteFileAsync(
            Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }
}
