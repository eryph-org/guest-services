using AwesomeAssertions;
using Eryph.GuestServices.CloudConfig;
using Eryph.GuestServices.Provisioning.Modules;
using Eryph.GuestServices.Provisioning.UserData;
using Eryph.GuestServices.Provisioning.Windows;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using CloudConfigModel = global::Eryph.GuestServices.CloudConfig.CloudConfig;

namespace Eryph.GuestServices.Provisioning.Tests.Modules;

public sealed class SshAuthorizedKeysModuleTests
{
    [Fact]
    public async Task Writes_top_level_keys_to_Administrator_by_default()
    {
        var os = Substitute.For<IWindowsOs>();
        var module = new SshAuthorizedKeysModule(NullLogger<SshAuthorizedKeysModule>.Instance);

        var config = new CloudConfigModel { SshAuthorizedKeys = ["ssh-rsa AAA"] };

        await module.ApplyAsync(
            ResolvedUserData.Empty(config),
            new TestModuleContext(os),
            CancellationToken.None);

        await os.Received().SetUserSshAuthorizedKeysAsync(
            "Administrator",
            Arg.Is<IReadOnlyList<string>>(k => k.Count == 1 && k[0] == "ssh-rsa AAA"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Prefers_sudo_user_for_top_level_keys()
    {
        var os = Substitute.For<IWindowsOs>();
        var module = new SshAuthorizedKeysModule(NullLogger<SshAuthorizedKeysModule>.Instance);

        var config = new CloudConfigModel
        {
            SshAuthorizedKeys = ["ssh-rsa AAA"],
            Users = [new UserConfig { Name = "alice", Sudo = "ALL" }],
        };

        await module.ApplyAsync(
            ResolvedUserData.Empty(config),
            new TestModuleContext(os),
            CancellationToken.None);

        await os.Received().SetUserSshAuthorizedKeysAsync(
            "alice",
            Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Writes_per_user_keys_to_each_user()
    {
        var os = Substitute.For<IWindowsOs>();
        var module = new SshAuthorizedKeysModule(NullLogger<SshAuthorizedKeysModule>.Instance);

        var config = new CloudConfigModel
        {
            Users =
            [
                new UserConfig { Name = "alice", SshAuthorizedKeys = ["alice-key"] },
                new UserConfig { Name = "bob", SshAuthorizedKeys = ["bob-key-1", "bob-key-2"] },
            ],
        };

        await module.ApplyAsync(
            ResolvedUserData.Empty(config),
            new TestModuleContext(os),
            CancellationToken.None);

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
        var module = new SshAuthorizedKeysModule(NullLogger<SshAuthorizedKeysModule>.Instance);

        var config = new CloudConfigModel
        {
            Users = [new UserConfig { Name = "alice" }],
        };

        await module.ApplyAsync(
            ResolvedUserData.Empty(config),
            new TestModuleContext(os),
            CancellationToken.None);

        await os.DidNotReceive().SetUserSshAuthorizedKeysAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }
}
