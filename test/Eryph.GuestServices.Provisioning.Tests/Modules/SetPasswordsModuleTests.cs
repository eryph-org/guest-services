using AwesomeAssertions;
using Eryph.GuestServices.CloudConfig;
using Eryph.GuestServices.Provisioning.Modules;
using Eryph.GuestServices.Provisioning.UserData;
using Eryph.GuestServices.Provisioning.Windows;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using CloudConfigModel = global::Eryph.GuestServices.CloudConfig.CloudConfig;

namespace Eryph.GuestServices.Provisioning.Tests.Modules;

public sealed class SetPasswordsModuleTests
{
    [Fact]
    public async Task Sets_passwords_from_chpasswd_users()
    {
        var os = Substitute.For<IWindowsOs>();
        var module = new SetPasswordsModule(NullLogger<SetPasswordsModule>.Instance);

        var config = new CloudConfigModel
        {
            Chpasswd = new ChpasswdConfig
            {
                Users =
                [
                    new ChpasswdListEntry { Name = "alice", Password = "secret" },
                    new ChpasswdListEntry { Name = "bob", Password = "other" },
                ],
            },
        };

        var result = await module.ApplyAsync(
            ResolvedUserData.Empty(config),
            new TestModuleContext(os),
            CancellationToken.None);

        result.Should().BeOfType<ModuleOutcome.Completed>();
        await os.Received().SetLocalUserPasswordAsync("alice", "secret", false, Arg.Any<CancellationToken>());
        await os.Received().SetLocalUserPasswordAsync("bob", "other", false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Generates_random_password_when_type_is_RANDOM()
    {
        var os = Substitute.For<IWindowsOs>();
        var module = new SetPasswordsModule(NullLogger<SetPasswordsModule>.Instance);

        var config = new CloudConfigModel
        {
            Chpasswd = new ChpasswdConfig
            {
                Users = [new ChpasswdListEntry { Name = "alice", Type = "RANDOM" }],
            },
        };

        await module.ApplyAsync(
            ResolvedUserData.Empty(config),
            new TestModuleContext(os),
            CancellationToken.None);

        await os.Received().SetLocalUserPasswordAsync(
            "alice",
            Arg.Is<string>(p => p.Length == 16),
            false,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Parses_legacy_list_form_user_colon_password()
    {
        var os = Substitute.For<IWindowsOs>();
        var module = new SetPasswordsModule(NullLogger<SetPasswordsModule>.Instance);

        var config = new CloudConfigModel
        {
            Chpasswd = new ChpasswdConfig
            {
                List = "alice:secret\nbob:other\n",
            },
        };

        await module.ApplyAsync(
            ResolvedUserData.Empty(config),
            new TestModuleContext(os),
            CancellationToken.None);

        await os.Received().SetLocalUserPasswordAsync("alice", "secret", false, Arg.Any<CancellationToken>());
        await os.Received().SetLocalUserPasswordAsync("bob", "other", false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Preserves_colons_inside_password_when_parsing_list()
    {
        var os = Substitute.For<IWindowsOs>();
        var module = new SetPasswordsModule(NullLogger<SetPasswordsModule>.Instance);

        var config = new CloudConfigModel
        {
            Chpasswd = new ChpasswdConfig { List = "alice:has:colons" },
        };

        await module.ApplyAsync(
            ResolvedUserData.Empty(config),
            new TestModuleContext(os),
            CancellationToken.None);

        await os.Received().SetLocalUserPasswordAsync("alice", "has:colons", false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Applies_password_shorthand_to_first_user()
    {
        var os = Substitute.For<IWindowsOs>();
        var module = new SetPasswordsModule(NullLogger<SetPasswordsModule>.Instance);

        var config = new CloudConfigModel
        {
            Users = [new UserConfig { Name = "alice" }],
            Password = "topsecret",
        };

        await module.ApplyAsync(
            ResolvedUserData.Empty(config),
            new TestModuleContext(os),
            CancellationToken.None);

        await os.Received().SetLocalUserPasswordAsync("alice", "topsecret", false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Falls_back_to_Administrator_when_no_users_configured()
    {
        var os = Substitute.For<IWindowsOs>();
        var module = new SetPasswordsModule(NullLogger<SetPasswordsModule>.Instance);

        var config = new CloudConfigModel { Password = "topsecret" };

        await module.ApplyAsync(
            ResolvedUserData.Empty(config),
            new TestModuleContext(os),
            CancellationToken.None);

        await os.Received().SetLocalUserPasswordAsync("Administrator", "topsecret", false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Returns_completed_when_no_passwords_configured()
    {
        var os = Substitute.For<IWindowsOs>();
        var module = new SetPasswordsModule(NullLogger<SetPasswordsModule>.Instance);

        var result = await module.ApplyAsync(
            ResolvedUserData.Empty(new CloudConfigModel()),
            new TestModuleContext(os),
            CancellationToken.None);

        result.Should().BeOfType<ModuleOutcome.Completed>();
        await os.DidNotReceive().SetLocalUserPasswordAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }
}
