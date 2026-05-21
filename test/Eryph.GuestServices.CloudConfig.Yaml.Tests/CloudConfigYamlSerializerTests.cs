using AwesomeAssertions;
using Eryph.ConfigModel;
using Eryph.GuestServices.CloudConfig;
using Eryph.GuestServices.CloudConfig.Yaml;
using YamlDotNet.Core;

namespace Eryph.GuestServices.CloudConfig.Yaml.Tests;

public class CloudConfigYamlSerializerTests
{
    [Fact]
    public void Deserialize_ScalarRuncmd_ReturnsShellCommandEntry()
    {
        const string yaml = "runcmd: echo hi";

        var config = CloudConfigYamlSerializer.Deserialize(yaml);

        config.Runcmd.Should().ContainSingle().Which.Should().Match<RuncmdEntry>(
            entry => entry.IsShellCommand && entry.Command == "echo hi" && entry.Argv == null);
    }

    [Fact]
    public void Deserialize_RuncmdSequenceOfScalars_ReturnsShellCommandEntries()
    {
        const string yaml = """
                            runcmd:
                            - echo hi
                            - echo bye
                            """;

        var config = CloudConfigYamlSerializer.Deserialize(yaml);

        config.Runcmd.Should().SatisfyRespectively(
            first =>
            {
                first.IsShellCommand.Should().BeTrue();
                first.Command.Should().Be("echo hi");
                first.Argv.Should().BeNull();
            },
            second =>
            {
                second.IsShellCommand.Should().BeTrue();
                second.Command.Should().Be("echo bye");
                second.Argv.Should().BeNull();
            });
    }

    [Fact]
    public void Deserialize_RuncmdSequenceOfArrays_ReturnsArgvEntries()
    {
        const string yaml = """
                            runcmd:
                            - [echo, hi]
                            - [echo, bye]
                            """;

        var config = CloudConfigYamlSerializer.Deserialize(yaml);

        config.Runcmd.Should().SatisfyRespectively(
            first =>
            {
                first.IsShellCommand.Should().BeFalse();
                first.Command.Should().BeNull();
                first.Argv.Should().Equal("echo", "hi");
            },
            second =>
            {
                second.IsShellCommand.Should().BeFalse();
                second.Command.Should().BeNull();
                second.Argv.Should().Equal("echo", "bye");
            });
    }

    [Fact]
    public void Deserialize_UserAsString_ReturnsUserWithNameOnly()
    {
        const string yaml = """
                            users:
                            - admin
                            """;

        var config = CloudConfigYamlSerializer.Deserialize(yaml);

        config.Users.Should().ContainSingle().Which.Name.Should().Be("admin");
    }

    [Fact]
    public void Deserialize_UserGroupsAsString_PromotesToList()
    {
        const string yaml = """
                            users:
                            - name: admin
                              groups: Administrators
                            """;

        var config = CloudConfigYamlSerializer.Deserialize(yaml);

        config.Users.Should().ContainSingle().Which.Groups.Should().Equal("Administrators");
    }

    [Fact]
    public void Deserialize_UserGroupsAsList_KeepsList()
    {
        const string yaml = """
                            users:
                            - name: admin
                              groups:
                              - Administrators
                              - Users
                            """;

        var config = CloudConfigYamlSerializer.Deserialize(yaml);

        config.Users.Should().ContainSingle().Which.Groups.Should().Equal("Administrators", "Users");
    }

    [Fact]
    public void Deserialize_SshAuthorizedKeysAsScalar_PromotesToList()
    {
        const string yaml = "ssh_authorized_keys: ssh-rsa AAAA";

        var config = CloudConfigYamlSerializer.Deserialize(yaml);

        config.SshAuthorizedKeys.Should().Equal("ssh-rsa AAAA");
    }

    [Fact]
    public void Deserialize_SshAuthorizedKeysAsList_KeepsList()
    {
        const string yaml = """
                            ssh_authorized_keys:
                            - ssh-rsa AAAA
                            - ssh-rsa BBBB
                            """;

        var config = CloudConfigYamlSerializer.Deserialize(yaml);

        config.SshAuthorizedKeys.Should().Equal("ssh-rsa AAAA", "ssh-rsa BBBB");
    }

    [Fact]
    public void Deserialize_UserSshAuthorizedKeysAsScalar_PromotesToList()
    {
        const string yaml = """
                            users:
                            - name: admin
                              ssh_authorized_keys: ssh-rsa AAAA
                            """;

        var config = CloudConfigYamlSerializer.Deserialize(yaml);

        config.Users.Should().ContainSingle().Which.SshAuthorizedKeys.Should().Equal("ssh-rsa AAAA");
    }

    [Fact]
    public void Deserialize_WriteFilePermissionsAsUnquotedOctal_NormalizesToFourDigitString()
    {
        const string yaml = """
                            write_files:
                            - path: /tmp/x
                              content: hi
                              permissions: 0644
                            """;

        var config = CloudConfigYamlSerializer.Deserialize(yaml);

        config.WriteFiles.Should().ContainSingle().Which.Should().Match<WriteFileConfig>(
            w => w.Path == "/tmp/x" && w.Content == "hi" && w.Permissions == "0644");
    }

    [Fact]
    public void Deserialize_WriteFilePermissionsAsQuotedOctal_KeepsString()
    {
        const string yaml = """
                            write_files:
                            - path: /tmp/x
                              content: hi
                              permissions: "0755"
                            """;

        var config = CloudConfigYamlSerializer.Deserialize(yaml);

        config.WriteFiles.Should().ContainSingle().Which.Permissions.Should().Be("0755");
    }

    [Fact]
    public void Deserialize_WriteFilePermissionsWithoutLeadingZero_PadsToFourDigits()
    {
        const string yaml = """
                            write_files:
                            - path: /tmp/x
                              content: hi
                              permissions: 644
                            """;

        var config = CloudConfigYamlSerializer.Deserialize(yaml);

        config.WriteFiles.Should().ContainSingle().Which.Permissions.Should().Be("0644");
    }

    [Fact]
    public void Deserialize_WriteFilePermissionsWith0oPrefix_StripsPrefixAndPads()
    {
        const string yaml = """
                            write_files:
                            - path: /tmp/x
                              content: hi
                              permissions: "0o755"
                            """;

        var config = CloudConfigYamlSerializer.Deserialize(yaml);

        config.WriteFiles.Should().ContainSingle().Which.Permissions.Should().Be("0755");
    }

    [Fact]
    public void Deserialize_WriteFilePermissionsWithNonOctalDigit_Throws()
    {
        const string yaml = """
                            write_files:
                            - path: /tmp/x
                              content: hi
                              permissions: 0deadbeef
                            """;

        var act = () => CloudConfigYamlSerializer.Deserialize(yaml);

        act.Should().Throw<InvalidConfigException>()
            .WithInnerException<YamlException>();
    }

    [Fact]
    public void Deserialize_WriteFilePermissionsWithDigit9_Throws()
    {
        const string yaml = """
                            write_files:
                            - path: /tmp/x
                              content: hi
                              permissions: "0759"
                            """;

        var act = () => CloudConfigYamlSerializer.Deserialize(yaml);

        act.Should().Throw<InvalidConfigException>()
            .WithInnerException<YamlException>();
    }

    [Fact]
    public void Deserialize_WithCloudConfigHeader_StripsHeader()
    {
        const string yaml = """
                            #cloud-config
                            hostname: my-host
                            """;

        var config = CloudConfigYamlSerializer.Deserialize(yaml);

        config.Hostname.Should().Be("my-host");
    }

    [Fact]
    public void Deserialize_WithCloudConfigHeaderAndLeadingBlankLines_StripsHeader()
    {
        const string yaml = "\n  \n#cloud-config\nhostname: my-host\n";

        var config = CloudConfigYamlSerializer.Deserialize(yaml);

        config.Hostname.Should().Be("my-host");
    }

    [Fact]
    public void Deserialize_WithoutCloudConfigHeader_ParsesNormally()
    {
        const string yaml = "hostname: my-host";

        var config = CloudConfigYamlSerializer.Deserialize(yaml);

        config.Hostname.Should().Be("my-host");
    }

    [Fact]
    public void Deserialize_MalformedYaml_ThrowsInvalidConfigException()
    {
        const string yaml = """
                            users:
                            - name: ]
                            """;

        var act = () => CloudConfigYamlSerializer.Deserialize(yaml);

        act.Should().Throw<InvalidConfigException>()
            .WithMessage("*line 2*column*")
            .WithInnerException<YamlException>();
    }

    [Fact]
    public void Deserialize_UnknownProperty_ThrowsInvalidConfigException()
    {
        const string yaml = "unknown_key: value";

        var act = () => CloudConfigYamlSerializer.Deserialize(yaml);

        act.Should().Throw<InvalidConfigException>()
            .WithMessage("*unknown_key*")
            .WithInnerException<YamlException>();
    }

    [Fact]
    public void Deserialize_ComplexConfig_ReturnsConfig()
    {
        const string yaml = """
                            #cloud-config
                            hostname: my-host
                            fqdn: my-host.example.com
                            preserve_hostname: false
                            users:
                            - admin
                            - name: alice
                              passwd: secret
                              lock_passwd: false
                              groups:
                              - Administrators
                              ssh_authorized_keys: ssh-rsa AAA
                              sudo: ALL=(ALL) NOPASSWD:ALL
                            groups:
                            - name: devs
                              members:
                              - alice
                            chpasswd:
                              expire: false
                              users:
                              - name: alice
                                password: secret
                                type: text
                            password: rootpw
                            ssh_pwauth: true
                            ssh_authorized_keys:
                            - ssh-rsa AAA
                            - ssh-rsa BBB
                            write_files:
                            - path: /tmp/a
                              content: hi
                              owner: root:root
                              permissions: "0644"
                              encoding: b64
                              append: false
                              defer: true
                            runcmd:
                            - echo hi
                            - [echo, bye]
                            """;

        var config = CloudConfigYamlSerializer.Deserialize(yaml);

        config.Hostname.Should().Be("my-host");
        config.Fqdn.Should().Be("my-host.example.com");
        config.PreserveHostname.Should().BeFalse();

        config.Users.Should().SatisfyRespectively(
            first =>
            {
                first.Name.Should().Be("admin");
            },
            second =>
            {
                second.Name.Should().Be("alice");
                second.Passwd.Should().Be("secret");
                second.LockPasswd.Should().BeFalse();
                second.Groups.Should().Equal("Administrators");
                second.SshAuthorizedKeys.Should().Equal("ssh-rsa AAA");
                second.Sudo.Should().Be("ALL=(ALL) NOPASSWD:ALL");
            });

        config.Groups.Should().ContainSingle().Which.Should().Match<GroupConfig>(
            g => g.Name == "devs" && g.Members!.Count == 1 && g.Members[0] == "alice");

        config.Chpasswd.Should().NotBeNull();
        config.Chpasswd!.Expire.Should().BeFalse();
        config.Chpasswd.Users.Should().ContainSingle().Which.Should().Match<ChpasswdListEntry>(
            e => e.Name == "alice" && e.Password == "secret" && e.Type == "text");

        config.Password.Should().Be("rootpw");
        config.SshPwauth.Should().BeTrue();
        config.SshAuthorizedKeys.Should().Equal("ssh-rsa AAA", "ssh-rsa BBB");

        config.WriteFiles.Should().ContainSingle().Which.Should().Match<WriteFileConfig>(
            w => w.Path == "/tmp/a"
                 && w.Content == "hi"
                 && w.Owner == "root:root"
                 && w.Permissions == "0644"
                 && w.Encoding == "b64"
                 && w.Append == false
                 && w.Defer == true);

        config.Runcmd.Should().SatisfyRespectively(
            first =>
            {
                first.IsShellCommand.Should().BeTrue();
                first.Command.Should().Be("echo hi");
            },
            second =>
            {
                second.IsShellCommand.Should().BeFalse();
                second.Argv.Should().Equal("echo", "bye");
            });
    }
}
