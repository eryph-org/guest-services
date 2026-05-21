using AwesomeAssertions;
using Eryph.GuestServices.CloudConfig;
using Eryph.GuestServices.Provisioning.UserData;

namespace Eryph.GuestServices.Provisioning.Tests.UserData;

public sealed class CloudConfigMergeTests
{
    [Fact]
    public void Scalar_RightWinsWhenSet()
    {
        var left = new CloudConfig.CloudConfig { Hostname = "left", Fqdn = "fqdn-left" };
        var right = new CloudConfig.CloudConfig { Hostname = "right" };

        var merged = CloudConfigMergeProxy.Merge(left, right);

        merged.Hostname.Should().Be("right");
        merged.Fqdn.Should().Be("fqdn-left"); // right was null -> keep left
    }

    [Fact]
    public void Runcmd_Concatenates()
    {
        var left = new CloudConfig.CloudConfig
        {
            Runcmd =
            [
                new RuncmdEntry { IsShellCommand = true, Command = "echo left" },
            ],
        };
        var right = new CloudConfig.CloudConfig
        {
            Runcmd =
            [
                new RuncmdEntry { IsShellCommand = true, Command = "echo right" },
            ],
        };

        var merged = CloudConfigMergeProxy.Merge(left, right);

        merged.Runcmd.Should().NotBeNull().And.HaveCount(2);
        merged.Runcmd![0].Command.Should().Be("echo left");
        merged.Runcmd[1].Command.Should().Be("echo right");
    }

    [Fact]
    public void Users_SameName_RightReplacesScalarsAndConcatsLists()
    {
        var left = new CloudConfig.CloudConfig
        {
            Users =
            [
                new UserConfig
                {
                    Name = "admin",
                    Passwd = "oldhash",
                    Groups = ["Administrators"],
                    SshAuthorizedKeys = ["ssh-ed25519 key-A"],
                },
                new UserConfig { Name = "guest" },
            ],
        };
        var right = new CloudConfig.CloudConfig
        {
            Users =
            [
                new UserConfig
                {
                    Name = "admin",
                    Passwd = "newhash",
                    Groups = ["Users"],
                    SshAuthorizedKeys = ["ssh-ed25519 key-B"],
                },
                new UserConfig { Name = "service" },
            ],
        };

        var merged = CloudConfigMergeProxy.Merge(left, right);

        merged.Users.Should().NotBeNull().And.HaveCount(3, "admin merges, guest + service stay");
        var admin = merged.Users!.Single(u => u.Name == "admin");
        admin.Passwd.Should().Be("newhash", "scalar takes the later value");
        admin.Groups.Should().Equal(["Administrators", "Users"], "list-typed fields concatenate inside merged user");
        admin.SshAuthorizedKeys.Should().HaveCount(2);
    }

    [Fact]
    public void SshAuthorizedKeys_Concatenate()
    {
        var left = new CloudConfig.CloudConfig { SshAuthorizedKeys = ["key-A"] };
        var right = new CloudConfig.CloudConfig { SshAuthorizedKeys = ["key-B"] };

        var merged = CloudConfigMergeProxy.Merge(left, right);

        merged.SshAuthorizedKeys.Should().Equal(["key-A", "key-B"]);
    }

    [Fact]
    public void Chpasswd_DeepMerges()
    {
        var left = new CloudConfig.CloudConfig
        {
            Chpasswd = new ChpasswdConfig
            {
                Expire = true,
                Users = [new ChpasswdListEntry { Name = "admin", Password = "old" }],
            },
        };
        var right = new CloudConfig.CloudConfig
        {
            Chpasswd = new ChpasswdConfig
            {
                Users = [new ChpasswdListEntry { Name = "admin", Password = "new", Type = "text" }],
            },
        };

        var merged = CloudConfigMergeProxy.Merge(left, right);

        merged.Chpasswd!.Expire.Should().BeTrue("left expire is preserved when right is null");
        merged.Chpasswd.Users.Should().ContainSingle();
        merged.Chpasswd.Users![0].Password.Should().Be("new");
        merged.Chpasswd.Users[0].Type.Should().Be("text");
    }

    [Fact]
    public void Empty_LeftAndRight_ReturnsEmpty()
    {
        var merged = CloudConfigMergeProxy.Merge(new CloudConfig.CloudConfig(), new CloudConfig.CloudConfig());

        merged.Hostname.Should().BeNull();
        merged.Runcmd.Should().BeNull();
        merged.Users.Should().BeNull();
    }
}
