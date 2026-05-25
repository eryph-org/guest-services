using System.IO.Compression;
using System.Text;
using AwesomeAssertions;
using Eryph.GuestServices.CloudConfig;
using Eryph.GuestServices.Provisioning.Modules;
using Eryph.GuestServices.Provisioning.Tests.Reporting;
using Eryph.GuestServices.Provisioning.UserData;
using Eryph.GuestServices.Provisioning.Windows;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using CloudConfigModel = global::Eryph.GuestServices.CloudConfig.CloudConfig;

namespace Eryph.GuestServices.Provisioning.Tests.Modules;

public sealed class WriteFilesModuleTests
{
    [Fact]
    public async Task Writes_plain_text_content()
    {
        var os = Substitute.For<IWindowsOs>();
        os.TranslateUnixPath("/etc/foo").Returns(@"C:\etc\foo");

        var module = new WriteFilesModule(NullLogger<WriteFilesModule>.Instance);
        var config = new CloudConfigModel
        {
            WriteFiles = [new WriteFileConfig { Path = "/etc/foo", Content = "hello" }],
        };

        var result = await module.ApplyAsync(
            ResolvedUserData.Empty(config),
            new TestModuleContext(os),
            CancellationToken.None);

        result.Should().BeOfType<ModuleOutcome.Completed>();
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
        var module = new WriteFilesModule(NullLogger<WriteFilesModule>.Instance);
        var config = new CloudConfigModel
        {
            WriteFiles = [new WriteFileConfig { Path = "/etc/foo", Content = b64, Encoding = "b64" }],
        };

        await module.ApplyAsync(
            ResolvedUserData.Empty(config),
            new TestModuleContext(os),
            CancellationToken.None);

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

        var module = new WriteFilesModule(NullLogger<WriteFilesModule>.Instance);
        var config = new CloudConfigModel
        {
            WriteFiles = [new WriteFileConfig { Path = "/etc/foo", Content = encoded, Encoding = "gz+b64" }],
        };

        await module.ApplyAsync(
            ResolvedUserData.Empty(config),
            new TestModuleContext(os),
            CancellationToken.None);

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

        var module = new WriteFilesModule(NullLogger<WriteFilesModule>.Instance);
        var config = new CloudConfigModel
        {
            WriteFiles = [new WriteFileConfig { Path = "/etc/foo", Content = "x", Append = true }],
        };

        await module.ApplyAsync(
            ResolvedUserData.Empty(config),
            new TestModuleContext(os),
            CancellationToken.None);

        await os.Received().WriteFileAsync(
            @"C:\etc\foo", Arg.Any<byte[]>(), true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Applies_owner_when_set()
    {
        var os = Substitute.For<IWindowsOs>();
        os.TranslateUnixPath("/etc/foo").Returns(@"C:\etc\foo");

        var module = new WriteFilesModule(NullLogger<WriteFilesModule>.Instance);
        var config = new CloudConfigModel
        {
            WriteFiles = [new WriteFileConfig { Path = "/etc/foo", Content = "x", Owner = "alice:devs" }],
        };

        await module.ApplyAsync(
            ResolvedUserData.Empty(config),
            new TestModuleContext(os),
            CancellationToken.None);

        await os.Received().SetFileOwnerAsync(@"C:\etc\foo", "alice:devs", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Does_not_call_set_owner_when_owner_absent()
    {
        var os = Substitute.For<IWindowsOs>();
        os.TranslateUnixPath("/etc/foo").Returns(@"C:\etc\foo");

        var module = new WriteFilesModule(NullLogger<WriteFilesModule>.Instance);
        var config = new CloudConfigModel
        {
            WriteFiles = [new WriteFileConfig { Path = "/etc/foo", Content = "x" }],
        };

        await module.ApplyAsync(
            ResolvedUserData.Empty(config),
            new TestModuleContext(os),
            CancellationToken.None);

        await os.DidNotReceive().SetFileOwnerAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // Regression: WriteFilesModule used to LogWarning('permissions ... are
    // ignored on Windows beyond basic ACLs') and skip the field entirely,
    // despite WindowsOs.SetPosixPermissionsAsync being implemented and wired
    // for exactly this translation (commit 0635c5c, "POSIX file permissions:
    // translate to NTFS ACLs"). The cloud-config wrapper existed but the call
    // site didn't invoke it — the user docs agent flagged this as an
    // implementation gap. This test makes the gap visible: cloud-config
    // permissions reach the OS layer, where the existing wrapper handles
    // the POSIX → NTFS-ACL translation.
    [Fact]
    public async Task Applies_posix_permissions_via_SetPosixPermissionsAsync()
    {
        var os = Substitute.For<IWindowsOs>();
        os.TranslateUnixPath("/etc/marker.txt").Returns(@"C:\etc\marker.txt");

        var module = new WriteFilesModule(NullLogger<WriteFilesModule>.Instance);
        var config = new CloudConfigModel
        {
            WriteFiles =
            [
                new WriteFileConfig
                {
                    Path = "/etc/marker.txt",
                    Content = "hello",
                    Permissions = "0644",
                },
            ],
        };

        await module.ApplyAsync(
            ResolvedUserData.Empty(config),
            new TestModuleContext(os),
            CancellationToken.None);

        await os.Received().SetPosixPermissionsAsync(
            @"C:\etc\marker.txt",
            "0644",
            null,
            Arg.Any<CancellationToken>());
        // Falls through SetFileOwnerAsync — the comprehensive PosixPermissions
        // call already sets the owner if provided.
        await os.DidNotReceive().SetFileOwnerAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Applies_posix_permissions_and_owner_in_one_call()
    {
        var os = Substitute.For<IWindowsOs>();
        os.TranslateUnixPath("/etc/marker.txt").Returns(@"C:\etc\marker.txt");

        var module = new WriteFilesModule(NullLogger<WriteFilesModule>.Instance);
        var config = new CloudConfigModel
        {
            WriteFiles =
            [
                new WriteFileConfig
                {
                    Path = "/etc/marker.txt",
                    Content = "hello",
                    Permissions = "0600",
                    Owner = "alice",
                },
            ],
        };

        await module.ApplyAsync(
            ResolvedUserData.Empty(config),
            new TestModuleContext(os),
            CancellationToken.None);

        // Single call carries both permissions and owner — no separate
        // SetFileOwnerAsync (would race against the ACL reset inside
        // SetPosixPermissionsAsync).
        await os.Received().SetPosixPermissionsAsync(
            @"C:\etc\marker.txt",
            "0600",
            "alice",
            Arg.Any<CancellationToken>());
        await os.DidNotReceive().SetFileOwnerAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Owner_only_falls_through_to_SetFileOwnerAsync()
    {
        // When the user supplied owner but no permissions, we use the
        // narrower SetFileOwnerAsync so the existing ACL is preserved —
        // SetPosixPermissionsAsync would reset it.
        var os = Substitute.For<IWindowsOs>();
        os.TranslateUnixPath("/etc/marker.txt").Returns(@"C:\etc\marker.txt");

        var module = new WriteFilesModule(NullLogger<WriteFilesModule>.Instance);
        var config = new CloudConfigModel
        {
            WriteFiles =
            [
                new WriteFileConfig
                {
                    Path = "/etc/marker.txt",
                    Content = "hello",
                    Owner = "alice",
                },
            ],
        };

        await module.ApplyAsync(
            ResolvedUserData.Empty(config),
            new TestModuleContext(os),
            CancellationToken.None);

        await os.Received().SetFileOwnerAsync(
            @"C:\etc\marker.txt", "alice", Arg.Any<CancellationToken>());
        await os.DidNotReceive().SetPosixPermissionsAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Fails_when_path_translation_throws_for_traversal()
    {
        var os = Substitute.For<IWindowsOs>();
        os.TranslateUnixPath("/../../Windows/notepad.exe")
            .Returns(_ => throw new ArgumentException("path contains '..'"));

        var module = new WriteFilesModule(NullLogger<WriteFilesModule>.Instance);
        var config = new CloudConfigModel
        {
            WriteFiles = [new WriteFileConfig { Path = "/../../Windows/notepad.exe", Content = "evil" }],
        };

        var result = await module.ApplyAsync(
            ResolvedUserData.Empty(config),
            new TestModuleContext(os),
            CancellationToken.None);

        result.Should().BeOfType<ModuleOutcome.Failed>()
            .Which.Reason.Should().Contain("path traversal");
        await os.DidNotReceive().WriteFileAsync(
            Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        await os.DidNotReceive().EnsureDirectoryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // Regression for review.md Finding 13. Cloud-init's documented contract:
    // an unknown `encoding:` value falls back to UTF-8 plaintext + warning.
    // We used to skip the entry entirely, so a typo like `gz-b64` (dash
    // instead of plus) silently dropped the operator's file. The new
    // behaviour treats the content as plain text.
    [Fact]
    public async Task Unknown_encoding_falls_back_to_plaintext()
    {
        var os = Substitute.For<IWindowsOs>();
        os.TranslateUnixPath("/tmp/x").Returns(@"C:\tmp\x");

        var logger = new CapturingLogger<WriteFilesModule>();
        var module = new WriteFilesModule(logger);
        var config = new CloudConfigModel
        {
            // Note the dash typo (cloud-init authors really do hit this).
            WriteFiles = [new WriteFileConfig { Path = "/tmp/x", Content = "hello world", Encoding = "gz-b64" }],
        };

        var result = await module.ApplyAsync(
            ResolvedUserData.Empty(config),
            new TestModuleContext(os),
            CancellationToken.None);

        result.Should().BeOfType<ModuleOutcome.Completed>();
        await os.Received().WriteFileAsync(
            @"C:\tmp\x",
            Arg.Is<byte[]>(b => b.SequenceEqual(Encoding.UTF8.GetBytes("hello world"))),
            false,
            Arg.Any<CancellationToken>());

        logger.Entries
            .Should()
            .ContainSingle(e => e.Level == LogLevel.Warning && e.Message.Contains("gz-b64"));
    }

    [Fact]
    public async Task Empty_content_with_unknown_encoding_writes_empty_file()
    {
        var os = Substitute.For<IWindowsOs>();
        os.TranslateUnixPath("/tmp/x").Returns(@"C:\tmp\x");

        var module = new WriteFilesModule(NullLogger<WriteFilesModule>.Instance);
        var config = new CloudConfigModel
        {
            WriteFiles = [new WriteFileConfig { Path = "/tmp/x", Content = null, Encoding = "weird" }],
        };

        await module.ApplyAsync(
            ResolvedUserData.Empty(config),
            new TestModuleContext(os),
            CancellationToken.None);

        await os.Received().WriteFileAsync(
            @"C:\tmp\x",
            Arg.Is<byte[]>(b => b.Length == 0),
            false,
            Arg.Any<CancellationToken>());
    }

    // Regression: WriteFilesModule must skip entries flagged with
    // defer: true — those are claimed by WriteFilesDeferredModule at Final.
    // The expectation matches cloud-init's cc_write_files (Config) /
    // cc_write_files_deferred (Final) split.
    [Fact]
    public async Task Skips_deferred_entries_at_config_stage()
    {
        var os = Substitute.For<IWindowsOs>();
        os.TranslateUnixPath("/etc/now").Returns(@"C:\etc\now");
        os.TranslateUnixPath("/etc/later").Returns(@"C:\etc\later");

        var module = new WriteFilesModule(NullLogger<WriteFilesModule>.Instance);
        var config = new CloudConfigModel
        {
            WriteFiles =
            [
                new WriteFileConfig { Path = "/etc/now", Content = "now" },
                new WriteFileConfig { Path = "/etc/later", Content = "later", Defer = true },
            ],
        };

        await module.ApplyAsync(
            ResolvedUserData.Empty(config),
            new TestModuleContext(os),
            CancellationToken.None);

        // Non-deferred entry is written.
        await os.Received().WriteFileAsync(
            @"C:\etc\now", Arg.Any<byte[]>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        // Deferred entry must not be touched at Config — even the path
        // translation should be skipped (no work whatsoever).
        await os.DidNotReceive().WriteFileAsync(
            @"C:\etc\later", Arg.Any<byte[]>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    // Regression: base64-encoded content must round-trip via byte[], never via
    // string. Any bytes > 0x7F (Latin-1 characters, binary data) must survive
    // intact; a string round-trip would mangle them via UTF-8 substitution.
    [Fact]
    public async Task Base64_preserves_non_utf8_bytes()
    {
        var os = Substitute.For<IWindowsOs>();
        os.TranslateUnixPath("/etc/bin").Returns(@"C:\etc\bin");

        // Bytes > 0x7F that are NOT valid UTF-8 sequences.
        var binary = new byte[] { 0x80, 0xFF, 0xFE, 0x00, 0xC0, 0x90 };
        var b64 = Convert.ToBase64String(binary);

        var module = new WriteFilesModule(NullLogger<WriteFilesModule>.Instance);
        var config = new CloudConfigModel
        {
            WriteFiles = [new WriteFileConfig { Path = "/etc/bin", Content = b64, Encoding = "base64" }],
        };

        await module.ApplyAsync(
            ResolvedUserData.Empty(config),
            new TestModuleContext(os),
            CancellationToken.None);

        await os.Received().WriteFileAsync(
            @"C:\etc\bin",
            Arg.Is<byte[]>(b => b.SequenceEqual(binary)),
            false,
            Arg.Any<CancellationToken>());
    }
}
