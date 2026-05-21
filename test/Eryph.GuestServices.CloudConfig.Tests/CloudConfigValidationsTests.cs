namespace Eryph.GuestServices.CloudConfig.Tests;

public class CloudConfigValidationsTests
{
    [Fact]
    public void ValidateCloudConfig_Empty_ReturnsSuccess()
    {
        var config = new CloudConfig();

        var result = CloudConfigValidations.ValidateCloudConfig(config);

        result.ShouldBeSuccess().Should().BeSameAs(config);
    }

    [Fact]
    public void ValidateCloudConfig_FullValid_ReturnsSuccess()
    {
        var config = new CloudConfig
        {
            Hostname = "myhost",
            Fqdn = "myhost.example.com",
            PreserveHostname = false,
            Users =
            [
                new UserConfig
                {
                    Name = "alice",
                    Passwd = "secret",
                    Groups = ["wheel", "docker"],
                    PrimaryGroup = "alice",
                    Shell = "/bin/bash",
                },
                new UserConfig { Name = "bob" },
            ],
            Groups =
            [
                new GroupConfig { Name = "wheel", Members = ["alice"] },
            ],
            Chpasswd = new ChpasswdConfig
            {
                Expire = false,
                Users =
                [
                    new ChpasswdListEntry { Name = "alice", Password = "x", Type = "text" },
                ],
            },
            SshAuthorizedKeys = ["ssh-ed25519 AAAA..."],
            WriteFiles =
            [
                new WriteFileConfig
                {
                    Path = "/etc/file",
                    Content = "hello",
                    Permissions = "0644",
                    Encoding = "b64",
                },
            ],
            Runcmd =
            [
                new RuncmdEntry { IsShellCommand = true, Command = "echo hi" },
                new RuncmdEntry { IsShellCommand = false, Argv = ["echo", "hi"] },
            ],
        };

        var result = CloudConfigValidations.ValidateCloudConfig(config);

        result.ShouldBeSuccess();
    }

    [Theory]
    [InlineData("host.local")]
    [InlineData("-host")]
    [InlineData("host-")]
    [InlineData("bad_host")]
    [InlineData("")]
    public void ValidateCloudConfig_InvalidHostname_ReturnsFail(string hostname)
    {
        var config = new CloudConfig { Hostname = hostname };

        var result = CloudConfigValidations.ValidateCloudConfig(config);

        result.ShouldBeFail().Should().NotBeEmpty();
    }

    [Theory]
    [InlineData("myhost")]
    [InlineData("MyHost")]
    [InlineData("host42")]
    public void ValidateCloudConfig_ValidHostname_ReturnsSuccess(string hostname)
    {
        var config = new CloudConfig { Hostname = hostname };

        var result = CloudConfigValidations.ValidateCloudConfig(config);

        result.ShouldBeSuccess();
    }

    [Fact]
    public void ValidateCloudConfig_TooLongHostname_ReturnsFail()
    {
        var config = new CloudConfig { Hostname = new string('a', 64) };

        var result = CloudConfigValidations.ValidateCloudConfig(config);

        result.ShouldBeFail().Flatten()
            .Should().Contain(e => e.Message.Contains("longer than"));
    }

    [Theory]
    [InlineData("host.example.com")]
    [InlineData("a.b.c.d.example.com")]
    public void ValidateCloudConfig_ValidFqdn_ReturnsSuccess(string fqdn)
    {
        var config = new CloudConfig { Fqdn = fqdn };

        var result = CloudConfigValidations.ValidateCloudConfig(config);

        result.ShouldBeSuccess();
    }

    [Theory]
    [InlineData("host..example.com")]
    [InlineData(".example.com")]
    [InlineData("host.")]
    public void ValidateCloudConfig_InvalidFqdn_ReturnsFail(string fqdn)
    {
        var config = new CloudConfig { Fqdn = fqdn };

        var result = CloudConfigValidations.ValidateCloudConfig(config);

        result.ShouldBeFail().Should().NotBeEmpty();
    }

    [Fact]
    public void ValidateCloudConfig_DuplicateUserNames_ReturnsFail()
    {
        var config = new CloudConfig
        {
            Users =
            [
                new UserConfig { Name = "alice" },
                new UserConfig { Name = "Alice" },
            ],
        };

        var result = CloudConfigValidations.ValidateCloudConfig(config);

        result.ShouldBeFail().Flatten()
            .Should().Contain(e => e.Message.Contains("not unique"));
    }

    [Fact]
    public void ValidateCloudConfig_UserWithoutName_ReturnsFail()
    {
        var config = new CloudConfig
        {
            Users = [new UserConfig { Passwd = "x" }],
        };

        var result = CloudConfigValidations.ValidateCloudConfig(config);

        result.ShouldBeFail().Flatten()
            .Should().Contain(e => e.Message.Contains("users[0]") && e.Message.Contains("cannot be empty"));
    }

    [Fact]
    public void ValidateCloudConfig_UserWithInvalidName_ReturnsFail()
    {
        var config = new CloudConfig
        {
            Users = [new UserConfig { Name = "bad/name" }],
        };

        var result = CloudConfigValidations.ValidateCloudConfig(config);

        result.ShouldBeFail().Flatten()
            .Should().Contain(e => e.Message.Contains("not a valid Windows or Linux user name"));
    }

    [Fact]
    public void ValidateCloudConfig_DuplicateGroupNames_ReturnsFail()
    {
        var config = new CloudConfig
        {
            Groups =
            [
                new GroupConfig { Name = "devs" },
                new GroupConfig { Name = "DEVS" },
            ],
        };

        var result = CloudConfigValidations.ValidateCloudConfig(config);

        result.ShouldBeFail().Flatten()
            .Should().Contain(e => e.Message.Contains("not unique"));
    }

    [Fact]
    public void ValidateCloudConfig_ChpasswdBothUsersAndList_ReturnsFail()
    {
        var config = new CloudConfig
        {
            Chpasswd = new ChpasswdConfig
            {
                Users = [new ChpasswdListEntry { Name = "alice", Password = "x" }],
                List = "alice:x",
            },
        };

        var result = CloudConfigValidations.ValidateCloudConfig(config);

        result.ShouldBeFail().Flatten()
            .Should().Contain(e => e.Message.Contains("both 'users' and 'list'"));
    }

    [Fact]
    public void ValidateCloudConfig_ChpasswdInvalidType_ReturnsFail()
    {
        var config = new CloudConfig
        {
            Chpasswd = new ChpasswdConfig
            {
                Users = [new ChpasswdListEntry { Name = "alice", Password = "x", Type = "invalid" }],
            },
        };

        var result = CloudConfigValidations.ValidateCloudConfig(config);

        result.ShouldBeFail().Flatten()
            .Should().Contain(e => e.Message.Contains("chpasswd type"));
    }

    [Fact]
    public void ValidateCloudConfig_WriteFileMissingPath_ReturnsFail()
    {
        var config = new CloudConfig
        {
            WriteFiles = [new WriteFileConfig { Content = "x" }],
        };

        var result = CloudConfigValidations.ValidateCloudConfig(config);

        result.ShouldBeFail().Flatten()
            .Should().Contain(e => e.Message.Contains("write_files[0]") && e.Message.Contains("cannot be empty"));
    }

    [Fact]
    public void ValidateCloudConfig_WriteFileInvalidPath_ReturnsFail()
    {
        var config = new CloudConfig
        {
            WriteFiles = [new WriteFileConfig { Path = "not-absolute" }],
        };

        var result = CloudConfigValidations.ValidateCloudConfig(config);

        result.ShouldBeFail().Flatten()
            .Should().Contain(e => e.Message.Contains("absolute Unix path"));
    }

    [Fact]
    public void ValidateCloudConfig_WriteFileInvalidPermissions_ReturnsFail()
    {
        var config = new CloudConfig
        {
            WriteFiles =
            [
                new WriteFileConfig { Path = "/etc/x", Permissions = "abc" },
            ],
        };

        var result = CloudConfigValidations.ValidateCloudConfig(config);

        result.ShouldBeFail().Flatten()
            .Should().Contain(e => e.Message.Contains("octal"));
    }

    [Fact]
    public void ValidateCloudConfig_WriteFileInvalidEncoding_ReturnsFail()
    {
        var config = new CloudConfig
        {
            WriteFiles =
            [
                new WriteFileConfig { Path = "/etc/x", Encoding = "rot13" },
            ],
        };

        var result = CloudConfigValidations.ValidateCloudConfig(config);

        result.ShouldBeFail().Flatten()
            .Should().Contain(e => e.Message.Contains("encoding 'rot13' is not supported"));
    }

    [Theory]
    [InlineData("base64")]
    [InlineData("b64")]
    [InlineData("gz")]
    [InlineData("gzip")]
    [InlineData("gz+b64")]
    [InlineData("gzip+base64")]
    public void ValidateCloudConfig_WriteFileValidEncoding_ReturnsSuccess(string encoding)
    {
        var config = new CloudConfig
        {
            WriteFiles =
            [
                new WriteFileConfig { Path = "/etc/x", Encoding = encoding },
            ],
        };

        var result = CloudConfigValidations.ValidateCloudConfig(config);

        result.ShouldBeSuccess();
    }

    [Fact]
    public void ValidateCloudConfig_RuncmdShellMissingCommand_ReturnsFail()
    {
        var config = new CloudConfig
        {
            Runcmd = [new RuncmdEntry { IsShellCommand = true }],
        };

        var result = CloudConfigValidations.ValidateCloudConfig(config);

        result.ShouldBeFail().Flatten()
            .Should().Contain(e => e.Message.Contains("runcmd[0]") && e.Message.Contains("command"));
    }

    [Fact]
    public void ValidateCloudConfig_RuncmdShellWithBothCommandAndArgv_ReturnsFail()
    {
        var config = new CloudConfig
        {
            Runcmd =
            [
                new RuncmdEntry { IsShellCommand = true, Command = "echo", Argv = ["echo"] },
            ],
        };

        var result = CloudConfigValidations.ValidateCloudConfig(config);

        result.ShouldBeFail().Flatten()
            .Should().Contain(e => e.Message.Contains("both 'command' and 'argv'"));
    }

    [Fact]
    public void ValidateCloudConfig_RuncmdExecEmptyArgv_ReturnsFail()
    {
        var config = new CloudConfig
        {
            Runcmd = [new RuncmdEntry { IsShellCommand = false, Argv = [] }],
        };

        var result = CloudConfigValidations.ValidateCloudConfig(config);

        result.ShouldBeFail().Flatten()
            .Should().Contain(e => e.Message.Contains("non-empty 'argv'"));
    }

    [Fact]
    public void ValidateCloudConfig_AggregatesMultipleErrors()
    {
        var config = new CloudConfig
        {
            Hostname = "Bad_Host",
            Users = [new UserConfig { Name = "bad/name" }],
            WriteFiles = [new WriteFileConfig { Path = "not-absolute" }],
        };

        var result = CloudConfigValidations.ValidateCloudConfig(config);

        var errors = result.ShouldBeFail().Flatten();
        errors.Should().Contain(e => e.Message.Contains("Bad_Host"));
        errors.Should().Contain(e => e.Message.Contains("users[0]"));
        errors.Should().Contain(e => e.Message.Contains("write_files[0]"));
    }
}
