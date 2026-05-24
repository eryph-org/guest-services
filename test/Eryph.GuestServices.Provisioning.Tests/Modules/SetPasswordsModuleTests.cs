using AwesomeAssertions;
using Eryph.GuestServices.CloudConfig;
using Eryph.GuestServices.Provisioning.Configuration;
using Eryph.GuestServices.Provisioning.DataSources;
using Eryph.GuestServices.Provisioning.Modules;
using Eryph.GuestServices.Provisioning.UserData;
using Eryph.GuestServices.Provisioning.Windows;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using CloudConfigModel = global::Eryph.GuestServices.CloudConfig.CloudConfig;

namespace Eryph.GuestServices.Provisioning.Tests.Modules;

public sealed class SetPasswordsModuleTests
{
    // The shorthand path targets the resolved default user; the chpasswd
    // paths name explicit users and never consult the resolver. A real
    // DefaultUserResolver with default settings reproduces the historical
    // "first admin / Administrator" behaviour these tests assert.
    private static SetPasswordsModule Build(ProvisioningSettings? settings = null) =>
        new(
            NullLogger<SetPasswordsModule>.Instance,
            new DefaultUserResolver(
                settings ?? new ProvisioningSettings(),
                NullLogger<DefaultUserResolver>.Instance));

    [Fact]
    public async Task Sets_passwords_from_chpasswd_users()
    {
        var os = Substitute.For<IWindowsOs>();
        var module = Build();

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
        await os.Received().SetLocalUserPasswordAsync("alice", "secret", true, Arg.Any<CancellationToken>());
        await os.Received().SetLocalUserPasswordAsync("bob", "other", true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Generates_random_password_when_type_is_RANDOM()
    {
        var os = Substitute.For<IWindowsOs>();
        var module = Build();

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
            true,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Parses_legacy_list_form_user_colon_password()
    {
        var os = Substitute.For<IWindowsOs>();
        var module = Build();

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

        await os.Received().SetLocalUserPasswordAsync("alice", "secret", true, Arg.Any<CancellationToken>());
        await os.Received().SetLocalUserPasswordAsync("bob", "other", true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Preserves_colons_inside_password_when_parsing_list()
    {
        var os = Substitute.For<IWindowsOs>();
        var module = Build();

        var config = new CloudConfigModel
        {
            Chpasswd = new ChpasswdConfig { List = "alice:has:colons" },
        };

        await module.ApplyAsync(
            ResolvedUserData.Empty(config),
            new TestModuleContext(os),
            CancellationToken.None);

        await os.Received().SetLocalUserPasswordAsync("alice", "has:colons", true, Arg.Any<CancellationToken>());
    }

    // The shorthand now targets the resolved default user (layer 1: first
    // sudo-enabled user in `users:`), not merely the first declared user.
    // A declared admin therefore receives the top-level password.
    [Fact]
    public async Task Applies_password_shorthand_to_declared_admin_user()
    {
        var os = Substitute.For<IWindowsOs>();
        var module = Build();

        var config = new CloudConfigModel
        {
            Users = [new UserConfig { Name = "alice", Sudo = ["ALL"] }],
            Password = "topsecret",
        };

        await module.ApplyAsync(
            ResolvedUserData.Empty(config),
            new TestModuleContext(os),
            CancellationToken.None);

        await os.Received().SetLocalUserPasswordAsync("alice", "topsecret", true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Falls_back_to_Administrator_when_no_users_configured()
    {
        var os = Substitute.For<IWindowsOs>();
        var module = Build();

        var config = new CloudConfigModel { Password = "topsecret" };

        await module.ApplyAsync(
            ResolvedUserData.Empty(config),
            new TestModuleContext(os),
            CancellationToken.None);

        await os.Received().SetLocalUserPasswordAsync("Administrator", "topsecret", true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Returns_completed_when_no_passwords_configured()
    {
        var os = Substitute.For<IWindowsOs>();
        var module = Build();

        var result = await module.ApplyAsync(
            ResolvedUserData.Empty(new CloudConfigModel()),
            new TestModuleContext(os),
            CancellationToken.None);

        result.Should().BeOfType<ModuleOutcome.Completed>();
        await os.DidNotReceive().SetLocalUserPasswordAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    // Cloud-init's chpasswd.expire defaults to true; when the operator opts
    // out, the password must not be flagged "must change at next logon".
    [Fact]
    public async Task Chpasswd_expire_false_disables_must_change_flag()
    {
        var os = Substitute.For<IWindowsOs>();
        var module = Build();

        var config = new CloudConfigModel
        {
            Chpasswd = new ChpasswdConfig
            {
                Expire = false,
                Users = [new ChpasswdListEntry { Name = "alice", Password = "secret" }],
            },
        };

        await module.ApplyAsync(
            ResolvedUserData.Empty(config),
            new TestModuleContext(os),
            CancellationToken.None);

        await os.Received().SetLocalUserPasswordAsync(
            "alice", "secret", false, Arg.Any<CancellationToken>());
    }

    // The default is true even when the operator omits the flag entirely:
    // mirrors cloud-init's cc_set_passwords default of `expire: true`.
    [Fact]
    public async Task Chpasswd_expire_omitted_defaults_to_must_change_flag()
    {
        var os = Substitute.For<IWindowsOs>();
        var module = Build();

        var config = new CloudConfigModel
        {
            Chpasswd = new ChpasswdConfig
            {
                Users = [new ChpasswdListEntry { Name = "alice", Password = "secret" }],
            },
        };

        await module.ApplyAsync(
            ResolvedUserData.Empty(config),
            new TestModuleContext(os),
            CancellationToken.None);

        await os.Received().SetLocalUserPasswordAsync(
            "alice", "secret", true, Arg.Any<CancellationToken>());
    }

    // No chpasswd block at all — the password shorthand still uses the
    // default-true expire flag (no Chpasswd config => Expire?.Value is null
    // => defaults to true).
    [Fact]
    public async Task Password_shorthand_without_chpasswd_block_uses_default_must_change_true()
    {
        var os = Substitute.For<IWindowsOs>();
        var module = Build();

        var config = new CloudConfigModel { Password = "topsecret" };

        await module.ApplyAsync(
            ResolvedUserData.Empty(config),
            new TestModuleContext(os),
            CancellationToken.None);

        await os.Received().SetLocalUserPasswordAsync(
            "Administrator", "topsecret", true, Arg.Any<CancellationToken>());
    }

    // Cloud-init's cc_set_passwords accepts the exact-case tokens R / RANDOM
    // after the colon in the chpasswd.list form. We mirror exact-case so
    // operators using documented cloud-init syntax see the same behaviour.
    [Theory]
    [InlineData("RANDOM")]
    [InlineData("R")]
    public async Task ChpasswdList_RandomToken_GeneratesPassword(string token)
    {
        var os = Substitute.For<IWindowsOs>();
        var module = Build();

        var config = new CloudConfigModel
        {
            Chpasswd = new ChpasswdConfig { List = $"bob:{token}" },
        };

        await module.ApplyAsync(
            ResolvedUserData.Empty(config),
            new TestModuleContext(os),
            CancellationToken.None);

        // The password actually set should be a 16-char generated value,
        // not the literal token.
        await os.Received().SetLocalUserPasswordAsync(
            "bob",
            Arg.Is<string>(p => p.Length == 16 && p != token),
            Arg.Any<bool>(),
            Arg.Any<CancellationToken>());
    }

    // Regression: a colon inside the password produces a literal value, and
    // even if RANDOM appears as a substring it must not be tokenised. Cloud-
    // init splits the first colon and treats everything after as a literal
    // unless it matches the exact `R` / `RANDOM` token.
    [Fact]
    public async Task ChpasswdList_LiteralColon_PreservesRandomSubstring()
    {
        var os = Substitute.For<IWindowsOs>();
        var module = Build();

        var config = new CloudConfigModel
        {
            Chpasswd = new ChpasswdConfig { List = "bob:literal:colon:RANDOM" },
        };

        await module.ApplyAsync(
            ResolvedUserData.Empty(config),
            new TestModuleContext(os),
            CancellationToken.None);

        await os.Received().SetLocalUserPasswordAsync(
            "bob", "literal:colon:RANDOM", Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    // Layer 1 of the resolver: an explicit sudo-enabled user in `users:` wins
    // over a settings-configured default-user name for the `password:`
    // shorthand. (`alice` here has no sudo, so this proves the SETTINGS name is
    // only used when no admin is declared — see the next test.)
    [Fact]
    public async Task Password_shorthand_targets_sudo_user_over_settings_default()
    {
        var os = Substitute.For<IWindowsOs>();
        var module = Build(new ProvisioningSettings
        {
            DefaultUser = new DefaultUserSettings { Name = "imageadmin" },
        });

        var config = new CloudConfigModel
        {
            Users = [new UserConfig { Name = "alice", Sudo = ["ALL"] }],
            Password = "topsecret",
        };

        await module.ApplyAsync(
            ResolvedUserData.Empty(config),
            new TestModuleContext(os),
            CancellationToken.None);

        await os.Received().SetLocalUserPasswordAsync("alice", "topsecret", true, Arg.Any<CancellationToken>());
        await os.DidNotReceive().SetLocalUserPasswordAsync(
            "imageadmin", Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    // Layer 3 of the resolver: with no `users:` admin declared, the shorthand
    // targets the image-baked DefaultUser.Name from settings rather than the
    // bare "Administrator" fallback.
    [Fact]
    public async Task Password_shorthand_uses_settings_default_user_name_when_no_admin_declared()
    {
        var os = Substitute.For<IWindowsOs>();
        var module = Build(new ProvisioningSettings
        {
            DefaultUser = new DefaultUserSettings { Name = "imageadmin" },
        });

        var config = new CloudConfigModel { Password = "topsecret" };

        await module.ApplyAsync(
            ResolvedUserData.Empty(config),
            new TestModuleContext(os),
            CancellationToken.None);

        await os.Received().SetLocalUserPasswordAsync("imageadmin", "topsecret", true, Arg.Any<CancellationToken>());
    }

    // Proves the shorthand target is sourced from IDefaultUserResolver (not a
    // hardcoded string): a mock resolver returns an arbitrary name and the
    // password lands on exactly that name.
    [Fact]
    public async Task Password_shorthand_uses_name_returned_by_resolver()
    {
        var os = Substitute.For<IWindowsOs>();
        var resolver = Substitute.For<IDefaultUserResolver>();
        resolver.Resolve(Arg.Any<CloudConfigModel>(), Arg.Any<DataSourceResult>())
            .Returns("resolved-account");
        var module = new SetPasswordsModule(NullLogger<SetPasswordsModule>.Instance, resolver);

        var config = new CloudConfigModel { Password = "topsecret" };

        await module.ApplyAsync(
            ResolvedUserData.Empty(config),
            new TestModuleContext(os),
            CancellationToken.None);

        await os.Received().SetLocalUserPasswordAsync(
            "resolved-account", "topsecret", true, Arg.Any<CancellationToken>());
    }
}
