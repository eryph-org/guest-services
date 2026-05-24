using System.Text;
using AwesomeAssertions;
using Eryph.GuestServices.CloudConfig;
using Eryph.GuestServices.Provisioning.Modules;
using Eryph.GuestServices.Provisioning.UserData;
using Eryph.GuestServices.Provisioning.Windows;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using CloudConfigModel = global::Eryph.GuestServices.CloudConfig.CloudConfig;

namespace Eryph.GuestServices.Provisioning.Tests.Modules;

public sealed class WriteFilesDeferredModuleTests
{
    [Fact]
    public async Task Writes_only_deferred_entries()
    {
        var os = Substitute.For<IWindowsOs>();
        os.TranslateUnixPath("/etc/now").Returns(@"C:\etc\now");
        os.TranslateUnixPath("/etc/later").Returns(@"C:\etc\later");

        var module = new WriteFilesDeferredModule(NullLogger<WriteFilesDeferredModule>.Instance);
        var config = new CloudConfigModel
        {
            WriteFiles =
            [
                new WriteFileConfig { Path = "/etc/now", Content = "now" },
                new WriteFileConfig { Path = "/etc/later", Content = "later", Defer = true },
            ],
        };

        var result = await module.ApplyAsync(
            ResolvedUserData.Empty(config),
            new TestModuleContext(os),
            CancellationToken.None);

        result.Should().BeOfType<ModuleOutcome.Completed>();
        // Only the deferred entry is written at Final.
        await os.Received().WriteFileAsync(
            @"C:\etc\later",
            Arg.Is<byte[]>(b => Encoding.UTF8.GetString(b) == "later"),
            false,
            Arg.Any<CancellationToken>());
        await os.DidNotReceive().WriteFileAsync(
            @"C:\etc\now", Arg.Any<byte[]>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Honors_owner_and_permissions_on_deferred_entries()
    {
        // Per-entry processing is shared with WriteFilesModule via
        // WriteFilesProcessor — this confirms the deferred path exercises it.
        var os = Substitute.For<IWindowsOs>();
        os.TranslateUnixPath("/home/alice/.ssh/authorized_keys")
            .Returns(@"C:\Users\alice\.ssh\authorized_keys");

        var module = new WriteFilesDeferredModule(NullLogger<WriteFilesDeferredModule>.Instance);
        var config = new CloudConfigModel
        {
            WriteFiles =
            [
                new WriteFileConfig
                {
                    Path = "/home/alice/.ssh/authorized_keys",
                    Content = "ssh-ed25519 AAAA…",
                    Permissions = "0600",
                    Owner = "alice",
                    Defer = true,
                },
            ],
        };

        await module.ApplyAsync(
            ResolvedUserData.Empty(config),
            new TestModuleContext(os),
            CancellationToken.None);

        await os.Received().SetPosixPermissionsAsync(
            @"C:\Users\alice\.ssh\authorized_keys",
            "0600",
            "alice",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Returns_completed_when_no_entries_are_deferred()
    {
        var os = Substitute.For<IWindowsOs>();
        os.TranslateUnixPath(Arg.Any<string>()).Returns(call => @"C:\etc\foo");

        var module = new WriteFilesDeferredModule(NullLogger<WriteFilesDeferredModule>.Instance);
        var config = new CloudConfigModel
        {
            WriteFiles = [new WriteFileConfig { Path = "/etc/foo", Content = "x" }],
        };

        var result = await module.ApplyAsync(
            ResolvedUserData.Empty(config),
            new TestModuleContext(os),
            CancellationToken.None);

        result.Should().BeOfType<ModuleOutcome.Completed>();
        await os.DidNotReceive().WriteFileAsync(
            Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Returns_completed_when_write_files_is_null()
    {
        var os = Substitute.For<IWindowsOs>();
        var module = new WriteFilesDeferredModule(NullLogger<WriteFilesDeferredModule>.Instance);

        var result = await module.ApplyAsync(
            ResolvedUserData.Empty(new CloudConfigModel()),
            new TestModuleContext(os),
            CancellationToken.None);

        result.Should().BeOfType<ModuleOutcome.Completed>();
    }

    [Fact]
    public async Task Fails_when_deferred_entry_attempts_path_traversal()
    {
        var os = Substitute.For<IWindowsOs>();
        os.TranslateUnixPath("/../../Windows/system32")
            .Returns(_ => throw new ArgumentException("path contains '..'"));

        var module = new WriteFilesDeferredModule(NullLogger<WriteFilesDeferredModule>.Instance);
        var config = new CloudConfigModel
        {
            WriteFiles =
            [
                new WriteFileConfig
                {
                    Path = "/../../Windows/system32",
                    Content = "evil",
                    Defer = true,
                },
            ],
        };

        var result = await module.ApplyAsync(
            ResolvedUserData.Empty(config),
            new TestModuleContext(os),
            CancellationToken.None);

        result.Should().BeOfType<ModuleOutcome.Failed>()
            .Which.Reason.Should().Contain("path traversal");
    }
}
