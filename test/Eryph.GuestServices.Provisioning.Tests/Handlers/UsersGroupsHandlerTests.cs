using AwesomeAssertions;
using Eryph.GuestServices.CloudConfig;
using Eryph.GuestServices.Provisioning.Handlers;
using Eryph.GuestServices.Provisioning.Stages;
using Eryph.GuestServices.Provisioning.Windows;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using CloudConfigModel = global::Eryph.GuestServices.CloudConfig.CloudConfig;

namespace Eryph.GuestServices.Provisioning.Tests.Handlers;

public sealed class UsersGroupsHandlerTests
{
    [Fact]
    public async Task Creates_missing_groups_and_skips_existing()
    {
        var os = Substitute.For<IWindowsOs>();
        os.LocalGroupExistsAsync("devs", Arg.Any<CancellationToken>()).Returns(false);
        os.LocalGroupExistsAsync("ops", Arg.Any<CancellationToken>()).Returns(true);

        var handler = new UsersGroupsHandler(NullLogger<UsersGroupsHandler>.Instance);
        var config = new CloudConfigModel
        {
            Groups =
            [
                new GroupConfig { Name = "devs", Members = ["alice"] },
                new GroupConfig { Name = "ops" },
            ],
        };

        var result = await handler.ApplyAsync(config, new TestHandlerContext(os), CancellationToken.None);

        result.Should().BeOfType<HandlerOutcome.Completed>();
        await os.Received().CreateLocalGroupAsync("devs", Arg.Any<CancellationToken>());
        await os.DidNotReceive().CreateLocalGroupAsync("ops", Arg.Any<CancellationToken>());
        await os.Received().AddUserToGroupAsync("alice", "devs", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Creates_missing_user_and_updates_existing_one()
    {
        var os = Substitute.For<IWindowsOs>();
        os.LocalUserExistsAsync("alice", Arg.Any<CancellationToken>()).Returns(false);
        os.LocalUserExistsAsync("bob", Arg.Any<CancellationToken>()).Returns(true);

        var handler = new UsersGroupsHandler(NullLogger<UsersGroupsHandler>.Instance);
        var config = new CloudConfigModel
        {
            Users =
            [
                new UserConfig { Name = "alice" },
                new UserConfig { Name = "bob", HomeDir = "C:\\Users\\bob" },
            ],
        };

        await handler.ApplyAsync(config, new TestHandlerContext(os), CancellationToken.None);

        await os.Received().CreateLocalUserAsync(
            Arg.Is<LocalUserSpec>(s => s.Name == "alice"), Arg.Any<CancellationToken>());
        await os.DidNotReceive().CreateLocalUserAsync(
            Arg.Is<LocalUserSpec>(s => s.Name == "bob"), Arg.Any<CancellationToken>());
        await os.Received().UpdateLocalUserAsync(
            Arg.Is<LocalUserSpec>(s => s.Name == "bob" && s.HomeDir == "C:\\Users\\bob"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Sets_user_password_when_passwd_is_set()
    {
        var os = Substitute.For<IWindowsOs>();
        os.LocalUserExistsAsync("alice", Arg.Any<CancellationToken>()).Returns(false);

        var handler = new UsersGroupsHandler(NullLogger<UsersGroupsHandler>.Instance);
        var config = new CloudConfigModel
        {
            Users = [new UserConfig { Name = "alice", Passwd = "secret" }],
        };

        await handler.ApplyAsync(config, new TestHandlerContext(os), CancellationToken.None);

        await os.Received().SetLocalUserPasswordAsync("alice", "secret", false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Adds_user_to_groups_and_creates_missing_groups_on_the_fly()
    {
        var os = Substitute.For<IWindowsOs>();
        os.LocalUserExistsAsync("alice", Arg.Any<CancellationToken>()).Returns(false);
        os.LocalGroupExistsAsync("devs", Arg.Any<CancellationToken>()).Returns(false);

        var handler = new UsersGroupsHandler(NullLogger<UsersGroupsHandler>.Instance);
        var config = new CloudConfigModel
        {
            Users = [new UserConfig { Name = "alice", Groups = ["devs"] }],
        };

        await handler.ApplyAsync(config, new TestHandlerContext(os), CancellationToken.None);

        await os.Received().CreateLocalGroupAsync("devs", Arg.Any<CancellationToken>());
        await os.Received().AddUserToGroupAsync("alice", "devs", Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("ALL")]
    [InlineData("ALL=(ALL) NOPASSWD:ALL")]
    [InlineData("true")]
    public async Task Promotes_user_to_administrators_when_sudo_truthy(string sudo)
    {
        var os = Substitute.For<IWindowsOs>();
        os.LocalUserExistsAsync("alice", Arg.Any<CancellationToken>()).Returns(false);

        var handler = new UsersGroupsHandler(NullLogger<UsersGroupsHandler>.Instance);
        var config = new CloudConfigModel
        {
            Users = [new UserConfig { Name = "alice", Sudo = sudo }],
        };

        await handler.ApplyAsync(config, new TestHandlerContext(os), CancellationToken.None);

        await os.Received().EnsureUserInAdministratorsAsync("alice", Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("false")]
    [InlineData("FALSE")]
    [InlineData(" false ")]
    public async Task Does_not_promote_user_when_sudo_false(string sudo)
    {
        var os = Substitute.For<IWindowsOs>();
        os.LocalUserExistsAsync("alice", Arg.Any<CancellationToken>()).Returns(false);

        var handler = new UsersGroupsHandler(NullLogger<UsersGroupsHandler>.Instance);
        var config = new CloudConfigModel
        {
            Users = [new UserConfig { Name = "alice", Sudo = sudo }],
        };

        await handler.ApplyAsync(config, new TestHandlerContext(os), CancellationToken.None);

        await os.DidNotReceive().EnsureUserInAdministratorsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Marks_user_disabled_when_lock_passwd_true()
    {
        var os = Substitute.For<IWindowsOs>();
        os.LocalUserExistsAsync("alice", Arg.Any<CancellationToken>()).Returns(false);

        var handler = new UsersGroupsHandler(NullLogger<UsersGroupsHandler>.Instance);
        var config = new CloudConfigModel
        {
            Users = [new UserConfig { Name = "alice", LockPasswd = true }],
        };

        await handler.ApplyAsync(config, new TestHandlerContext(os), CancellationToken.None);

        await os.Received().CreateLocalUserAsync(
            Arg.Is<LocalUserSpec>(s => s.Name == "alice" && s.Disabled == true),
            Arg.Any<CancellationToken>());
    }
}
