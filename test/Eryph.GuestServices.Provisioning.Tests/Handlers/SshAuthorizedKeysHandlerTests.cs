using AwesomeAssertions;
using Eryph.GuestServices.CloudConfig;
using Eryph.GuestServices.Provisioning.Handlers;
using Eryph.GuestServices.Provisioning.Stages;
using Eryph.GuestServices.Provisioning.Windows;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using CloudConfigModel = global::Eryph.GuestServices.CloudConfig.CloudConfig;

namespace Eryph.GuestServices.Provisioning.Tests.Handlers;

public sealed class SshAuthorizedKeysHandlerTests
{
    [Fact]
    public async Task Writes_top_level_keys_to_Administrator_by_default()
    {
        var os = Substitute.For<IWindowsOs>();
        var handler = new SshAuthorizedKeysHandler(NullLogger<SshAuthorizedKeysHandler>.Instance);

        var config = new CloudConfigModel { SshAuthorizedKeys = ["ssh-rsa AAA"] };

        await handler.ApplyAsync(config, new TestHandlerContext(os), CancellationToken.None);

        await os.Received().SetUserSshAuthorizedKeysAsync(
            "Administrator",
            Arg.Is<IReadOnlyList<string>>(k => k.Count == 1 && k[0] == "ssh-rsa AAA"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Prefers_sudo_user_for_top_level_keys()
    {
        var os = Substitute.For<IWindowsOs>();
        var handler = new SshAuthorizedKeysHandler(NullLogger<SshAuthorizedKeysHandler>.Instance);

        var config = new CloudConfigModel
        {
            SshAuthorizedKeys = ["ssh-rsa AAA"],
            Users = [new UserConfig { Name = "alice", Sudo = "ALL" }],
        };

        await handler.ApplyAsync(config, new TestHandlerContext(os), CancellationToken.None);

        await os.Received().SetUserSshAuthorizedKeysAsync(
            "alice",
            Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Writes_per_user_keys_to_each_user()
    {
        var os = Substitute.For<IWindowsOs>();
        var handler = new SshAuthorizedKeysHandler(NullLogger<SshAuthorizedKeysHandler>.Instance);

        var config = new CloudConfigModel
        {
            Users =
            [
                new UserConfig { Name = "alice", SshAuthorizedKeys = ["alice-key"] },
                new UserConfig { Name = "bob", SshAuthorizedKeys = ["bob-key-1", "bob-key-2"] },
            ],
        };

        await handler.ApplyAsync(config, new TestHandlerContext(os), CancellationToken.None);

        await os.Received().SetUserSshAuthorizedKeysAsync(
            "alice",
            Arg.Is<IReadOnlyList<string>>(k => k.Count == 1 && k[0] == "alice-key"),
            Arg.Any<CancellationToken>());
        await os.Received().SetUserSshAuthorizedKeysAsync(
            "bob",
            Arg.Is<IReadOnlyList<string>>(k => k.Count == 2),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Skips_users_without_keys()
    {
        var os = Substitute.For<IWindowsOs>();
        var handler = new SshAuthorizedKeysHandler(NullLogger<SshAuthorizedKeysHandler>.Instance);

        var config = new CloudConfigModel
        {
            Users = [new UserConfig { Name = "alice" }],
        };

        await handler.ApplyAsync(config, new TestHandlerContext(os), CancellationToken.None);

        await os.DidNotReceive().SetUserSshAuthorizedKeysAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }
}
