using AwesomeAssertions;
using Eryph.GuestServices.CloudConfig;
using Eryph.GuestServices.Provisioning.Configuration;
using Eryph.GuestServices.Provisioning.Modules;
using Eryph.GuestServices.Provisioning.UserData;
using Eryph.GuestServices.Provisioning.Windows;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using CloudConfigModel = global::Eryph.GuestServices.CloudConfig.CloudConfig;

namespace Eryph.GuestServices.Provisioning.Tests.Modules;

public sealed class UsersGroupsModuleTests
{
    // Default settings (CreateIfMissing=false) reproduce the historical
    // behaviour the bulk of these tests assert. The CreateIfMissing tests pass
    // explicit settings. A real DefaultUserResolver is used so the resolved
    // name matches production resolution exactly.
    private static UsersGroupsModule Build(ProvisioningSettings? settings = null)
    {
        var resolved = settings ?? new ProvisioningSettings();
        return new UsersGroupsModule(
            NullLogger<UsersGroupsModule>.Instance,
            resolved,
            new DefaultUserResolver(resolved, NullLogger<DefaultUserResolver>.Instance));
    }

    [Fact]
    public async Task Creates_missing_groups_and_skips_existing()
    {
        var os = Substitute.For<IWindowsOs>();
        os.LocalGroupExistsAsync("devs", Arg.Any<CancellationToken>()).Returns(false);
        os.LocalGroupExistsAsync("ops", Arg.Any<CancellationToken>()).Returns(true);

        var module = Build();
        var config = new CloudConfigModel
        {
            Groups =
            [
                new GroupConfig { Name = "devs", Members = ["alice"] },
                new GroupConfig { Name = "ops" },
            ],
        };

        var result = await module.ApplyAsync(
            ResolvedUserData.Empty(config),
            new TestModuleContext(os),
            CancellationToken.None);

        result.Should().BeOfType<ModuleOutcome.Completed>();
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

        var module = Build();
        var config = new CloudConfigModel
        {
            Users =
            [
                new UserConfig { Name = "alice" },
                new UserConfig { Name = "bob", HomeDir = "C:\\Users\\bob" },
            ],
        };

        await module.ApplyAsync(
            ResolvedUserData.Empty(config),
            new TestModuleContext(os),
            CancellationToken.None);

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

        var module = Build();
        var config = new CloudConfigModel
        {
            Users = [new UserConfig { Name = "alice", Passwd = "secret" }],
        };

        await module.ApplyAsync(
            ResolvedUserData.Empty(config),
            new TestModuleContext(os),
            CancellationToken.None);

        await os.Received().SetLocalUserPasswordAsync("alice", "secret", false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Sets_user_password_from_plain_text_passwd()
    {
        var os = Substitute.For<IWindowsOs>();
        os.LocalUserExistsAsync("alice", Arg.Any<CancellationToken>()).Returns(false);

        var module = Build();
        var config = new CloudConfigModel
        {
            Users = [new UserConfig { Name = "alice", PlainTextPasswd = "plain-secret" }],
        };

        await module.ApplyAsync(
            ResolvedUserData.Empty(config),
            new TestModuleContext(os),
            CancellationToken.None);

        await os.Received().SetLocalUserPasswordAsync("alice", "plain-secret", false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Plain_text_passwd_wins_over_passwd_when_both_are_set()
    {
        var os = Substitute.For<IWindowsOs>();
        os.LocalUserExistsAsync("alice", Arg.Any<CancellationToken>()).Returns(false);

        var module = Build();
        var config = new CloudConfigModel
        {
            Users = [new UserConfig { Name = "alice", Passwd = "hashed", PlainTextPasswd = "plain" }],
        };

        await module.ApplyAsync(
            ResolvedUserData.Empty(config),
            new TestModuleContext(os),
            CancellationToken.None);

        await os.Received().SetLocalUserPasswordAsync("alice", "plain", false, Arg.Any<CancellationToken>());
        await os.DidNotReceive().SetLocalUserPasswordAsync(
            "alice", "hashed", Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Adds_user_to_groups_and_creates_missing_groups_on_the_fly()
    {
        var os = Substitute.For<IWindowsOs>();
        os.LocalUserExistsAsync("alice", Arg.Any<CancellationToken>()).Returns(false);
        os.LocalGroupExistsAsync("devs", Arg.Any<CancellationToken>()).Returns(false);

        var module = Build();
        var config = new CloudConfigModel
        {
            Users = [new UserConfig { Name = "alice", Groups = ["devs"] }],
        };

        await module.ApplyAsync(
            ResolvedUserData.Empty(config),
            new TestModuleContext(os),
            CancellationToken.None);

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

        var module = Build();
        var config = new CloudConfigModel
        {
            Users = [new UserConfig { Name = "alice", Sudo = [sudo] }],
        };

        await module.ApplyAsync(
            ResolvedUserData.Empty(config),
            new TestModuleContext(os),
            CancellationToken.None);

        await os.Received().EnsureUserInAdministratorsAsync("alice", Arg.Any<CancellationToken>());
    }

    // Locks the mixed-list policy documented on IsSudoEnabled: any non-
    // "false" entry promotes, even when other entries in the list are
    // "false". Cloud-init's per-rule semantics don't translate to Windows;
    // collapsing to the binary admin/non-admin decision is the only
    // platform-relevant answer.
    [Fact]
    public async Task Mixed_sudo_list_with_one_truthy_entry_promotes_user()
    {
        var os = Substitute.For<IWindowsOs>();
        os.LocalUserExistsAsync("alice", Arg.Any<CancellationToken>()).Returns(false);

        var module = Build();
        var config = new CloudConfigModel
        {
            Users =
            [
                new UserConfig
                {
                    Name = "alice",
                    Sudo = ["ALL=(ALL) NOPASSWD:ALL", "false"],
                },
            ],
        };

        await module.ApplyAsync(
            ResolvedUserData.Empty(config),
            new TestModuleContext(os),
            CancellationToken.None);

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

        var module = Build();
        var config = new CloudConfigModel
        {
            Users = [new UserConfig { Name = "alice", Sudo = [sudo] }],
        };

        await module.ApplyAsync(
            ResolvedUserData.Empty(config),
            new TestModuleContext(os),
            CancellationToken.None);

        await os.DidNotReceive().EnsureUserInAdministratorsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // Cloud-init carries GECOS as a Linux concept (comment column in
    // /etc/passwd). On Windows we map it to the NTUser full-name field —
    // visible as "Full name" in lusrmgr.msc — so cross-cloud cloud-config
    // round-trips cleanly without losing the operator's display value.
    [Fact]
    public async Task User_WithGecos_AppliesFullName()
    {
        var os = Substitute.For<IWindowsOs>();
        os.LocalUserExistsAsync("alice", Arg.Any<CancellationToken>()).Returns(false);

        var module = Build();
        var config = new CloudConfigModel
        {
            Users = [new UserConfig { Name = "alice", Gecos = "Alice Anderson" }],
        };

        await module.ApplyAsync(
            ResolvedUserData.Empty(config),
            new TestModuleContext(os),
            CancellationToken.None);

        await os.Received().CreateLocalUserAsync(
            Arg.Is<LocalUserSpec>(s =>
                s.Name == "alice"
                && s.FullName == "Alice Anderson"
                && s.Comment == "Alice Anderson"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task User_WithGecos_UpdatesExistingUser()
    {
        var os = Substitute.For<IWindowsOs>();
        os.LocalUserExistsAsync("alice", Arg.Any<CancellationToken>()).Returns(true);

        var module = Build();
        var config = new CloudConfigModel
        {
            Users = [new UserConfig { Name = "alice", Gecos = "Alice Anderson" }],
        };

        await module.ApplyAsync(
            ResolvedUserData.Empty(config),
            new TestModuleContext(os),
            CancellationToken.None);

        await os.Received().UpdateLocalUserAsync(
            Arg.Is<LocalUserSpec>(s => s.Name == "alice" && s.FullName == "Alice Anderson"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Marks_user_disabled_when_lock_passwd_true()
    {
        var os = Substitute.For<IWindowsOs>();
        os.LocalUserExistsAsync("alice", Arg.Any<CancellationToken>()).Returns(false);

        var module = Build();
        var config = new CloudConfigModel
        {
            Users = [new UserConfig { Name = "alice", LockPasswd = true }],
        };

        await module.ApplyAsync(
            ResolvedUserData.Empty(config),
            new TestModuleContext(os),
            CancellationToken.None);

        await os.Received().CreateLocalUserAsync(
            Arg.Is<LocalUserSpec>(s => s.Name == "alice" && s.Disabled == true),
            Arg.Any<CancellationToken>());
    }

    // RFC 0018 CreateIfMissing: with no matching `users:` entry and the
    // account absent, the resolved default user is auto-created and promoted
    // to Administrators (the null-Groups default). Enables the OpenStack-style
    // "password-only cloud-config provisions a known admin" flow.
    [Fact]
    public async Task CreateIfMissing_creates_resolved_default_user_in_administrators()
    {
        var os = Substitute.For<IWindowsOs>();
        os.LocalUserExistsAsync("imageadmin", Arg.Any<CancellationToken>()).Returns(false);

        var module = Build(new ProvisioningSettings
        {
            DefaultUser = new DefaultUserSettings { Name = "imageadmin", CreateIfMissing = true },
        });
        var config = new CloudConfigModel();

        await module.ApplyAsync(
            ResolvedUserData.Empty(config),
            new TestModuleContext(os),
            CancellationToken.None);

        await os.Received().CreateLocalUserAsync(
            Arg.Is<LocalUserSpec>(s => s.Name == "imageadmin"), Arg.Any<CancellationToken>());
        await os.Received().EnsureUserInAdministratorsAsync("imageadmin", Arg.Any<CancellationToken>());
    }

    // The custom DefaultUser.Groups list is honoured: a non-Administrators
    // group is created on the fly and the user added by name; Administrators is
    // still routed through the SID-correct helper.
    [Fact]
    public async Task CreateIfMissing_honors_custom_default_user_groups()
    {
        var os = Substitute.For<IWindowsOs>();
        os.LocalUserExistsAsync("imageadmin", Arg.Any<CancellationToken>()).Returns(false);
        os.LocalGroupExistsAsync("Remote Desktop Users", Arg.Any<CancellationToken>()).Returns(false);

        var module = Build(new ProvisioningSettings
        {
            DefaultUser = new DefaultUserSettings
            {
                Name = "imageadmin",
                CreateIfMissing = true,
                Groups = ["Administrators", "Remote Desktop Users"],
            },
        });
        var config = new CloudConfigModel();

        await module.ApplyAsync(
            ResolvedUserData.Empty(config),
            new TestModuleContext(os),
            CancellationToken.None);

        await os.Received().EnsureUserInAdministratorsAsync("imageadmin", Arg.Any<CancellationToken>());
        await os.Received().CreateLocalGroupAsync("Remote Desktop Users", Arg.Any<CancellationToken>());
        await os.Received().AddUserToGroupAsync("imageadmin", "Remote Desktop Users", Arg.Any<CancellationToken>());
    }

    // No duplicate creation when the resolved default user IS declared in
    // `users:` (case-insensitive match): the explicit entry is processed
    // normally and the auto-create step skips it.
    [Fact]
    public async Task CreateIfMissing_does_not_duplicate_when_user_declared_in_users()
    {
        var os = Substitute.For<IWindowsOs>();
        os.LocalUserExistsAsync("imageadmin", Arg.Any<CancellationToken>()).Returns(false);

        var module = Build(new ProvisioningSettings
        {
            DefaultUser = new DefaultUserSettings { Name = "imageadmin", CreateIfMissing = true },
        });
        var config = new CloudConfigModel
        {
            Users = [new UserConfig { Name = "IMAGEADMIN" }],
        };

        await module.ApplyAsync(
            ResolvedUserData.Empty(config),
            new TestModuleContext(os),
            CancellationToken.None);

        // Created exactly once — from the explicit `users:` entry, not a second
        // time by the auto-create step.
        await os.Received(1).CreateLocalUserAsync(
            Arg.Any<LocalUserSpec>(), Arg.Any<CancellationToken>());
    }

    // CreateIfMissing skips when the account already exists on the box: no
    // create call, but the run still completes.
    [Fact]
    public async Task CreateIfMissing_skips_when_default_user_already_exists()
    {
        var os = Substitute.For<IWindowsOs>();
        os.LocalUserExistsAsync("imageadmin", Arg.Any<CancellationToken>()).Returns(true);

        var module = Build(new ProvisioningSettings
        {
            DefaultUser = new DefaultUserSettings { Name = "imageadmin", CreateIfMissing = true },
        });
        var config = new CloudConfigModel();

        await module.ApplyAsync(
            ResolvedUserData.Empty(config),
            new TestModuleContext(os),
            CancellationToken.None);

        await os.DidNotReceive().CreateLocalUserAsync(
            Arg.Any<LocalUserSpec>(), Arg.Any<CancellationToken>());
    }

    // Regression: CreateIfMissing=false (today's default) never auto-creates,
    // even with a configured DefaultUser.Name and no declared users.
    [Fact]
    public async Task CreateIfMissing_false_never_auto_creates()
    {
        var os = Substitute.For<IWindowsOs>();
        os.LocalUserExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);

        var module = Build(new ProvisioningSettings
        {
            DefaultUser = new DefaultUserSettings { Name = "imageadmin", CreateIfMissing = false },
        });
        var config = new CloudConfigModel();

        await module.ApplyAsync(
            ResolvedUserData.Empty(config),
            new TestModuleContext(os),
            CancellationToken.None);

        await os.DidNotReceive().CreateLocalUserAsync(
            Arg.Any<LocalUserSpec>(), Arg.Any<CancellationToken>());
        await os.DidNotReceive().EnsureUserInAdministratorsAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
