using AwesomeAssertions;
using Eryph.GuestServices.Provisioning.Configuration;
using Eryph.GuestServices.Provisioning.DataSources;
using Eryph.GuestServices.Provisioning.Modules;
using Microsoft.Extensions.Logging.Abstractions;
using CloudConfigModel = global::Eryph.GuestServices.CloudConfig.CloudConfig;
using UserConfig = global::Eryph.GuestServices.CloudConfig.UserConfig;

namespace Eryph.GuestServices.Provisioning.Tests.Modules;

public sealed class DefaultUserResolverTests
{
    private static DefaultUserResolver Build(ProvisioningSettings? settings = null) =>
        new(settings ?? new ProvisioningSettings(), NullLogger<DefaultUserResolver>.Instance);

    private static DataSourceResult DataSource(string? defaultUserName = null) =>
        new()
        {
            SourceName = "test",
            InstanceId = "i-1",
            DefaultUserName = defaultUserName,
        };

    [Fact]
    public void Layer1_explicit_sudo_user_wins()
    {
        var config = new CloudConfigModel
        {
            Users = [new UserConfig { Name = "alice", Sudo = ["ALL"] }],
        };

        Build().Resolve(config, DataSource()).Should().Be("alice");
    }

    [Fact]
    public void Layer1_first_sudo_user_wins_over_later_ones()
    {
        var config = new CloudConfigModel
        {
            Users =
            [
                new UserConfig { Name = "bob" },                    // no sudo — skipped
                new UserConfig { Name = "alice", Sudo = ["ALL"] },  // first admin
                new UserConfig { Name = "carol", Sudo = ["ALL"] },
            ],
        };

        Build().Resolve(config, DataSource()).Should().Be("alice");
    }

    [Fact]
    public void Explicit_user_wins_over_settings()
    {
        var config = new CloudConfigModel
        {
            Users = [new UserConfig { Name = "alice", Sudo = ["ALL"] }],
        };
        var settings = new ProvisioningSettings
        {
            DefaultUser = new DefaultUserSettings { Name = "image-admin" },
        };

        Build(settings).Resolve(config, DataSource()).Should().Be("alice");
    }

    [Fact]
    public void Layer2_datasource_used_when_no_admin_user()
    {
        var config = new CloudConfigModel
        {
            Users = [new UserConfig { Name = "bob" }],  // no sudo
        };

        Build().Resolve(config, DataSource(defaultUserName: "ds-admin"))
            .Should().Be("ds-admin");
    }

    [Fact]
    public void Layer3_settings_used_when_no_user_and_no_datasource()
    {
        var settings = new ProvisioningSettings
        {
            DefaultUser = new DefaultUserSettings { Name = "image-admin" },
        };

        Build(settings).Resolve(new CloudConfigModel(), DataSource())
            .Should().Be("image-admin");
    }

    [Fact]
    public void Datasource_wins_over_settings()
    {
        var settings = new ProvisioningSettings
        {
            DefaultUser = new DefaultUserSettings { Name = "image-admin" },
        };

        Build(settings).Resolve(new CloudConfigModel(), DataSource(defaultUserName: "ds-admin"))
            .Should().Be("ds-admin");
    }

    [Fact]
    public void Layer4_administrator_fallback_when_nothing_set()
    {
        Build().Resolve(new CloudConfigModel(), DataSource())
            .Should().Be("Administrator");
    }

    [Fact]
    public void Non_admin_users_do_not_satisfy_layer1()
    {
        var config = new CloudConfigModel
        {
            Users = [new UserConfig { Name = "bob", Sudo = ["false"] }],
        };

        // Falls through to the Administrator fallback.
        Build().Resolve(config, DataSource()).Should().Be("Administrator");
    }
}
