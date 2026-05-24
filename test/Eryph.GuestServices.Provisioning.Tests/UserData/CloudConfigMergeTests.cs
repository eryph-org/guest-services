using AwesomeAssertions;
using Eryph.GuestServices.CloudConfig;
using Eryph.GuestServices.CloudConfig.Linux;
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
    public void YumRepos_DictMerge_KeepsLeftOnly_ReplacesShared_AndDeepMergesRecords()
    {
        // Dict-merge semantics for [CloudInitRecord]-valued dictionaries:
        // shared keys deep-merge their record fields, novel keys flow
        // through verbatim. This is the cloud-init convention for
        // apt.sources / yum_repos / puppet.conf.
        var left = new CloudConfig.CloudConfig
        {
            YumRepos = new Dictionary<string, YumRepoConfig>
            {
                ["epel"] = new() { Baseurl = "https://old/epel", Enabled = true, Priority = 10 },
                ["legacy"] = new() { Baseurl = "https://old/legacy" },
            },
        };
        var right = new CloudConfig.CloudConfig
        {
            YumRepos = new Dictionary<string, YumRepoConfig>
            {
                ["epel"] = new() { Baseurl = "https://new/epel", Gpgcheck = false },
                ["docker"] = new() { Baseurl = "https://new/docker", Enabled = true },
            },
        };

        var merged = CloudConfigMergeProxy.Merge(left, right);

        merged.YumRepos.Should().NotBeNull().And.HaveCount(3, "epel merges, legacy + docker stay");
        merged.YumRepos!["epel"].Baseurl.Should().Be("https://new/epel", "scalar takes right");
        merged.YumRepos["epel"].Enabled.Should().BeTrue("left's value preserved when right is null");
        merged.YumRepos["epel"].Priority.Should().Be(10);
        merged.YumRepos["epel"].Gpgcheck.Should().BeFalse();
        merged.YumRepos["legacy"].Baseurl.Should().Be("https://old/legacy");
        merged.YumRepos["docker"].Baseurl.Should().Be("https://new/docker");
    }

    [Fact]
    public void SshKeys_PlainDictMerge_RightReplacesSharedKeys()
    {
        // Dict-merge with a plain string value type uses straight key-by-key
        // replacement; no per-value deep merge is wired since strings are
        // scalars.
        var left = new CloudConfig.CloudConfig
        {
            SshKeys = new Dictionary<string, string>
            {
                ["rsa_private"] = "old-rsa",
                ["ed25519_private"] = "ed25519-key",
            },
        };
        var right = new CloudConfig.CloudConfig
        {
            SshKeys = new Dictionary<string, string>
            {
                ["rsa_private"] = "new-rsa",
                ["dsa_private"] = "dsa-key",
            },
        };

        var merged = CloudConfigMergeProxy.Merge(left, right);

        merged.SshKeys.Should().NotBeNull().And.HaveCount(3);
        merged.SshKeys!["rsa_private"].Should().Be("new-rsa");
        merged.SshKeys["ed25519_private"].Should().Be("ed25519-key");
        merged.SshKeys["dsa_private"].Should().Be("dsa-key");
    }

    [Fact]
    public void Empty_LeftAndRight_ReturnsEmpty()
    {
        var merged = CloudConfigMergeProxy.Merge(new CloudConfig.CloudConfig(), new CloudConfig.CloudConfig());

        merged.Hostname.Should().BeNull();
        merged.Runcmd.Should().BeNull();
        merged.Users.Should().BeNull();
    }

    // Tripwire for the Phase 1 source-gen migration. Before this test existed,
    // CloudConfigMerge was a hand-written record-initialiser that named only
    // 11 of CloudConfig's ~30 properties — every other field was silently
    // dropped on every merge (and the merge runs once per fragment, starting
    // from an empty CloudConfig, so even single-fragment payloads lost data).
    //
    // The test walks every property declared on CloudConfig, sets it to a
    // non-default value on the right-hand side, and asserts the merged
    // record carries that value through. Reflection is fine here — the
    // restriction is on production code (where the generator emits straight-
    // line C#). Tests are exactly the place to detect "did the generator's
    // coverage drop a property?".
    [Fact]
    public void Merge_EveryCloudConfigProperty_SurvivesFromRight()
    {
        var populated = BuildPopulatedCloudConfig();
        var merged = CloudConfigMergeProxy.Merge(new CloudConfig.CloudConfig(), populated);

        foreach (var prop in typeof(CloudConfig.CloudConfig).GetProperties())
        {
            if (prop.Name == "EqualityContract") continue;

            var expected = prop.GetValue(populated);
            var actual = prop.GetValue(merged);

            actual.Should().NotBeNull(
                $"property '{prop.Name}' was populated on the right-hand side but came back null after merge — " +
                $"the source-gen Merge does not cover this property.");

            // For value-typed scalars, equality is straightforward. For
            // collections / nested records we compare via BeEquivalentTo so
            // structural identity is enough (deep-merge of identical right-
            // only input is the right's value).
            actual.Should().BeEquivalentTo(expected,
                $"merged value of '{prop.Name}' should match the right-hand input.");
        }
    }

    // Sibling of Merge_EveryCloudConfigProperty_SurvivesFromRight that walks
    // every property declared on UserConfig. Phase 2A widened UserConfig's
    // schema from 12 to 23 fields; this tripwire catches a future addition
    // that the hand-rolled MergeUser helper forgets to wire (the helper is
    // hand-written because it's called via KeyedByName on Users, not by the
    // source-gen merge emitter directly).
    [Fact]
    public void Merge_EveryUserConfigProperty_SurvivesFromRight()
    {
        var populated = BuildPopulatedUserConfig();
        // Run the user through the same KeyedByName path the production code
        // takes — wrap in a single-user CloudConfig and merge from empty.
        var merged = CloudConfigMergeProxy.Merge(
            new CloudConfig.CloudConfig(),
            new CloudConfig.CloudConfig { Users = [populated] });

        merged.Users.Should().ContainSingle();
        var mergedUser = merged.Users![0];

        foreach (var prop in typeof(UserConfig).GetProperties())
        {
            if (prop.Name == "EqualityContract") continue;

            var expected = prop.GetValue(populated);
            var actual = prop.GetValue(mergedUser);

            actual.Should().NotBeNull(
                $"property '{prop.Name}' was populated on the right-hand UserConfig but came back null after merge — " +
                $"MergeUser does not cover this property.");
            actual.Should().BeEquivalentTo(expected,
                $"merged value of UserConfig.'{prop.Name}' should match the right-hand input.");
        }
    }

    // Walks every property on WriteFileConfig. Phase 2A attributed all the
    // existing fields with [CloudInitField]; this test pins the generator's
    // emit coverage for the nested write_files record.
    [Fact]
    public void Merge_EveryWriteFileConfigProperty_SurvivesFromRight()
    {
        var populated = new WriteFileConfig
        {
            Path = "/etc/example",
            Content = "hello\n",
            Owner = "root:root",
            Permissions = "0644",
            Encoding = "text/plain",
            Append = true,
            Defer = true,
        };

        // WriteFiles is a Concat-merged list at the root, so a left of empty
        // and a right with one entry gives a single-entry merged list whose
        // member is the source-gen MergeWriteFileConfig output via deep merge
        // — except WriteFiles is Concat (per the generator), so the entry
        // itself is the right entry verbatim. We assert by reflection across
        // the entry's properties either way.
        var merged = CloudConfigMergeProxy.Merge(
            new CloudConfig.CloudConfig(),
            new CloudConfig.CloudConfig { WriteFiles = [populated] });

        merged.WriteFiles.Should().ContainSingle();
        var mergedEntry = merged.WriteFiles![0];

        foreach (var prop in typeof(WriteFileConfig).GetProperties())
        {
            if (prop.Name == "EqualityContract") continue;

            var expected = prop.GetValue(populated);
            var actual = prop.GetValue(mergedEntry);

            actual.Should().NotBeNull(
                $"property '{prop.Name}' was populated on the right-hand WriteFileConfig but came back null after merge.");
            actual.Should().BeEquivalentTo(expected,
                $"merged value of WriteFileConfig.'{prop.Name}' should match the right-hand input.");
        }
    }

    private static UserConfig BuildPopulatedUserConfig() => new()
    {
        Name = "alice",
        Passwd = "$6$rounds$hash",
        PlainTextPasswd = "plain",
        HashedPasswd = "$6$rounds$hash",
        LockPasswd = false,
        Groups = ["users"],
        SshAuthorizedKeys = ["ssh-ed25519 key"],
        Inactive = "30",
        Shell = "/bin/bash",
        HomeDir = "/home/alice",
        PrimaryGroup = "alice",
        Sudo = ["ALL=(ALL) NOPASSWD:ALL"],
        System = false,
        Gecos = "Alice Wonderland",
        SshImportId = ["gh:alice"],
        SshRedirectUser = false,
        Expiredate = "2099-12-31",
        NoCreateHome = false,
        NoUserGroup = false,
        NoLogInit = false,
        SelinuxUser = "user_u",
        Uid = 4242,
        Snapuser = "alice@example.com",
    };

    private static CloudConfig.CloudConfig BuildPopulatedCloudConfig() => new()
    {
        Hostname = "host",
        Fqdn = "host.example.com",
        PreserveHostname = true,
        PreferFqdnOverHostname = true,
        Users = [new UserConfig { Name = "admin" }],
        Groups = [new GroupConfig { Name = "Administrators" }],
        Chpasswd = new ChpasswdConfig
        {
            Expire = true,
            Users = [new ChpasswdListEntry { Name = "admin", Password = "x" }],
            List = "admin:x",
        },
        Password = "p",
        SshPwauth = true,
        SshAuthorizedKeys = ["ssh-ed25519 key"],
        WriteFiles = [new WriteFileConfig { Path = "C:/a.txt", Content = "x" }],
        Runcmd = [new RuncmdEntry { IsShellCommand = true, Command = "echo" }],
        Growpart = new GrowpartConfig { Mode = "auto" },
        Ntp = new NtpConfig { Enabled = true },
        Timezone = "UTC",
        Locale = "en-US",
        Keyboard = new KeyboardConfig { Layout = "en-US" },
        License = new LicenseConfig { ProductKey = "k" },
        Apt = new AptConfig { Proxy = "http://proxy:8080" },
        AptPipelining = "default",
        Packages = "pkgs",
        PackageUpdate = true,
        PackageUpgrade = true,
        PackageRebootIfRequired = true,
        Snap = new SnapConfig { Commands = ["snap install foo"] },
        YumRepos = new Dictionary<string, YumRepoConfig>
        {
            ["epel"] = new() { Baseurl = "https://example/epel", Enabled = true },
        },
        YumRepoDir = "/etc/yum",
        DiskSetup = new Dictionary<string, DiskSetupEntry>
        {
            ["/dev/sdb"] = new() { TableType = "gpt", Overwrite = true },
        },
        FsSetup = [new FsSetupEntry { Device = "/dev/sdb1", Filesystem = "ext4" }],
        Mounts = [["/dev/sdb1", "/data", "ext4", "defaults", "0", "2"]],
        MountDefaultFields = ["none", "none", "auto", "defaults,nofail", "0", "2"],
        Swap = new SwapConfig { Filename = "/swap.img", Size = "1G" },
        ManageEtcHosts = "localhost",
        ManageResolvConf = true,
        ResolvConf = new ResolvConfConfig { Nameservers = ["1.1.1.1"] },
        Bootcmd = [new RuncmdEntry { IsShellCommand = true, Command = "echo boot" }],
        PowerState = new PowerStateConfig { Mode = "reboot" },
        PhoneHome = new PhoneHomeConfig { Url = "https://home", Tries = 3 },
        FinalMessage = "done",
        CaCerts = new CaCertsConfig { RemoveDefaults = true, Trusted = ["-----BEGIN CERTIFICATE-----..."] },
        DisableRoot = true,
        DisableRootOpts = "opts",
        Chef = new ChefConfig { ServerUrl = "https://chef", RunList = ["recipe[foo]"] },
        Ansible = new AnsibleConfig { InstallMethod = "pip" },
        Puppet = new PuppetConfig { Install = true, Version = "7.0" },
        SaltMinion = new SaltMinionConfig { PkgName = "salt-minion" },
        DisableEc2Metadata = true,
        Migrate = false,
        SshDeleteKeys = true,
        SshGenKeyTypes = ["ed25519"],
        SshImportId = ["gh:octocat"],
        SshKeys = new Dictionary<string, string>
        {
            ["rsa_private"] = "-----BEGIN RSA PRIVATE KEY-----...",
        },
        SshPublishHostKeys = new SshPublishHostKeysConfig { Enabled = true },
        ByobuByDefault = "system",
        ResizeRootfs = "noblock",
        LocaleConfigFile = "/etc/default/locale",
        RandomSeed = new RandomSeedConfig { File = "/dev/urandom", Data = "seed" },
        Output = new Dictionary<string, object?> { ["all"] = "| tee -a /var/log/cloud-init-output.log" },
        Reporting = new Dictionary<string, ReportingHandlerConfig>
        {
            ["webhook"] = new() { Type = "webhook", Endpoint = "https://hook" },
        },
        UpdateEtcHosts = true,
        UpdateHostname = true,
    };
}
