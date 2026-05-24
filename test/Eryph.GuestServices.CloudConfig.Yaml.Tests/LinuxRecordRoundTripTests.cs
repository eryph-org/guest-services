using AwesomeAssertions;
using Eryph.GuestServices.CloudConfig;
using Eryph.GuestServices.CloudConfig.Linux;
using Eryph.GuestServices.CloudConfig.Yaml;

namespace Eryph.GuestServices.CloudConfig.Yaml.Tests;

/// <summary>
/// Round-trip coverage for the Linux-typed records introduced in Phase 2.
/// These are acknowledged-but-no-op on Windows, but cloud-config YAML
/// produced by a Linux operator must still deserialise into the typed
/// shape so the merge layer and the source-generated inventory see the
/// structured contents rather than an opaque blob.
/// </summary>
public sealed class LinuxRecordRoundTripTests
{
    [Fact]
    public void Apt_with_sources_and_primary_deserialises_to_typed_record()
    {
        const string yaml = """
                            apt:
                              proxy: http://proxy:8080
                              preserve_sources_list: false
                              primary:
                                - arches: [default]
                                  uri: http://us.archive.ubuntu.com/ubuntu
                              sources:
                                docker:
                                  source: deb https://download.docker.com/linux/ubuntu jammy stable
                                  keyid: 0EBFCD88
                            """;

        var config = CloudConfigYamlSerializer.Deserialize(yaml);

        config.Apt.Should().NotBeNull();
        config.Apt!.Proxy.Should().Be("http://proxy:8080");
        config.Apt.PreserveSourcesList.Should().BeFalse();
        config.Apt.Primary.Should().ContainSingle();
        config.Apt.Primary![0].Arches.Should().Equal("default");
        config.Apt.Primary[0].Uri.Should().Be("http://us.archive.ubuntu.com/ubuntu");
        config.Apt.Sources.Should().ContainKey("docker");
        config.Apt.Sources!["docker"].Keyid.Should().Be("0EBFCD88");
    }

    [Fact]
    public void Snap_with_commands_and_assertions_deserialises_to_typed_record()
    {
        const string yaml = """
                            snap:
                              commands:
                                - snap install hello-world
                                - snap install firefox
                              assertions:
                                - "type: account..."
                                - "type: account-key..."
                            """;

        var config = CloudConfigYamlSerializer.Deserialize(yaml);

        config.Snap.Should().NotBeNull();
        config.Snap!.Commands.Should().Equal("snap install hello-world", "snap install firefox");
        config.Snap.Assertions.Should().HaveCount(2);
        config.Snap.Assertions![0].Should().StartWith("type: account");
    }

    [Fact]
    public void YumRepos_with_two_entries_deserialises_to_typed_dict()
    {
        const string yaml = """
                            yum_repos:
                              epel-release:
                                baseurl: https://download.fedoraproject.org/pub/epel/8/Everything/$basearch
                                name: EPEL
                                enabled: true
                                gpgcheck: true
                                gpgkey: file:///etc/pki/rpm-gpg/RPM-GPG-KEY-EPEL-8
                              docker-ce:
                                baseurl: https://download.docker.com/linux/centos/8/$basearch/stable
                                enabled: true
                                gpgcheck: false
                                priority: 1
                            """;

        var config = CloudConfigYamlSerializer.Deserialize(yaml);

        config.YumRepos.Should().NotBeNull().And.HaveCount(2);
        config.YumRepos!["epel-release"].Name.Should().Be("EPEL");
        config.YumRepos["epel-release"].Enabled.Should().BeTrue();
        config.YumRepos["docker-ce"].Priority.Should().Be(1);
        config.YumRepos["docker-ce"].Gpgcheck.Should().BeFalse();
    }

    [Fact]
    public void Bootcmd_with_mixed_shell_and_argv_uses_runcmd_shape()
    {
        // bootcmd is structurally identical to runcmd — same per-entry
        // converter handles scalar-string ↔ argv-list dispatch.
        const string yaml = """
                            bootcmd:
                              - echo first
                              - [echo, second]
                            """;

        var config = CloudConfigYamlSerializer.Deserialize(yaml);

        config.Bootcmd.Should().HaveCount(2);
        config.Bootcmd![0].IsShellCommand.Should().BeTrue();
        config.Bootcmd[0].Command.Should().Be("echo first");
        config.Bootcmd[1].IsShellCommand.Should().BeFalse();
        config.Bootcmd[1].Argv.Should().Equal("echo", "second");
    }

    [Fact]
    public void PhoneHome_with_scalar_post_promotes_to_list()
    {
        // Cloud-init accepts `post: all` (scalar) and `post: [key1, key2]`
        // (list). The StringListYamlConverter promotes the scalar form.
        const string yaml = """
                            phone_home:
                              url: https://example.com/hook
                              post: all
                              tries: 10
                            """;

        var config = CloudConfigYamlSerializer.Deserialize(yaml);

        config.PhoneHome.Should().NotBeNull();
        config.PhoneHome!.Url.Should().Be("https://example.com/hook");
        config.PhoneHome.Post.Should().Equal("all");
        config.PhoneHome.Tries.Should().Be(10);
    }

    [Fact]
    public void PhoneHome_with_list_post_keeps_list()
    {
        const string yaml = """
                            phone_home:
                              url: https://example.com/hook
                              post:
                                - pub_key_dsa
                                - pub_key_rsa
                                - fqdn
                            """;

        var config = CloudConfigYamlSerializer.Deserialize(yaml);

        config.PhoneHome!.Post.Should().Equal("pub_key_dsa", "pub_key_rsa", "fqdn");
    }

    [Fact]
    public void Chef_with_run_list_and_omnibus_url_deserialises()
    {
        const string yaml = """
                            chef:
                              server_url: https://chef.example.com/organizations/my-org
                              node_name: web-01
                              environment: production
                              run_list:
                                - recipe[apt]
                                - recipe[nginx::default]
                              install_type: omnibus
                              omnibus_url: https://omnitruck.chef.io/install.sh
                              omnibus_url_retries: 3
                              force_install: true
                            """;

        var config = CloudConfigYamlSerializer.Deserialize(yaml);

        config.Chef.Should().NotBeNull();
        config.Chef!.ServerUrl.Should().Be("https://chef.example.com/organizations/my-org");
        config.Chef.RunList.Should().Equal("recipe[apt]", "recipe[nginx::default]");
        config.Chef.OmnibusUrl.Should().Be("https://omnitruck.chef.io/install.sh");
        config.Chef.OmnibusUrlRetries.Should().Be(3);
        config.Chef.ForceInstall.Should().BeTrue();
    }

    [Fact]
    public void Puppet_with_nested_conf_deserialises_to_typed_dict_of_dicts()
    {
        const string yaml = """
                            puppet:
                              install: true
                              version: 7.0.0
                              collection: puppet7
                              conf:
                                main:
                                  server: puppet.example.com
                                  certname: web-01.example.com
                                agent:
                                  report: 'true'
                                  pluginsync: 'true'
                            """;

        var config = CloudConfigYamlSerializer.Deserialize(yaml);

        config.Puppet.Should().NotBeNull();
        config.Puppet!.Install.Should().BeTrue();
        config.Puppet.Version.Should().Be("7.0.0");
        config.Puppet.Conf.Should().NotBeNull().And.HaveCount(2);
        config.Puppet.Conf!["main"]["server"].Should().Be("puppet.example.com");
        config.Puppet.Conf["agent"]["pluginsync"].Should().Be("true");
    }

    [Fact]
    public void Mounts_with_two_entries_keeps_list_of_lists_shape()
    {
        const string yaml = """
                            mounts:
                              - [/dev/sdb1, /data, ext4, defaults, "0", "2"]
                              - [/dev/sdc1, /logs, ext4, "defaults,nofail", "0", "2"]
                            """;

        var config = CloudConfigYamlSerializer.Deserialize(yaml);

        config.Mounts.Should().HaveCount(2);
        config.Mounts![0].Should().Equal("/dev/sdb1", "/data", "ext4", "defaults", "0", "2");
        config.Mounts[1].Should().Equal("/dev/sdc1", "/logs", "ext4", "defaults,nofail", "0", "2");
    }

    [Fact]
    public void SshImportId_scalar_promotes_to_list()
    {
        // Top-level ssh_import_id accepts scalar-or-list per cloud-init's
        // documented schema; same converter pattern as ssh_authorized_keys.
        const string yaml = "ssh_import_id: gh:octocat";

        var config = CloudConfigYamlSerializer.Deserialize(yaml);

        config.SshImportId.Should().Equal("gh:octocat");
    }

    [Fact]
    public void SshImportId_list_keeps_list()
    {
        const string yaml = """
                            ssh_import_id:
                              - gh:octocat
                              - lp:alice
                            """;

        var config = CloudConfigYamlSerializer.Deserialize(yaml);

        config.SshImportId.Should().Equal("gh:octocat", "lp:alice");
    }

    [Fact]
    public void SshKeys_dict_round_trips()
    {
        const string yaml = """
                            ssh_keys:
                              rsa_private: |
                                -----BEGIN RSA PRIVATE KEY-----
                                XXXX
                                -----END RSA PRIVATE KEY-----
                              rsa_public: ssh-rsa AAAA host
                            """;

        var config = CloudConfigYamlSerializer.Deserialize(yaml);

        config.SshKeys.Should().NotBeNull().And.HaveCount(2);
        config.SshKeys!["rsa_private"].Should().Contain("BEGIN RSA PRIVATE KEY");
        config.SshKeys["rsa_public"].Should().Be("ssh-rsa AAAA host");
    }

    [Fact]
    public void CaCerts_supports_both_remove_defaults_spellings()
    {
        // Canonical snake-case form lands on RemoveDefaults; legacy hyphen
        // spelling lands on RemoveDefaultsLegacy (alias wired externally).
        const string yaml = """
                            ca_certs:
                              remove_defaults: true
                              trusted:
                                - "-----BEGIN CERTIFICATE-----\nXYZ\n-----END CERTIFICATE-----"
                            """;

        var config = CloudConfigYamlSerializer.Deserialize(yaml);

        config.CaCerts.Should().NotBeNull();
        config.CaCerts!.RemoveDefaults.Should().BeTrue();
        config.CaCerts.RemoveDefaultsLegacy.Should().BeNull();
        config.CaCerts.Trusted.Should().ContainSingle();
    }

    [Fact]
    public void CaCerts_legacy_hyphen_spelling_lands_on_alias_property()
    {
        const string yaml = """
                            ca_certs:
                              remove-defaults: true
                            """;

        var config = CloudConfigYamlSerializer.Deserialize(yaml);

        config.CaCerts.Should().NotBeNull();
        config.CaCerts!.RemoveDefaultsLegacy.Should().BeTrue();
        config.CaCerts.RemoveDefaults.Should().BeNull();
    }

    [Fact]
    public void Ansible_with_nested_pull_deserialises()
    {
        const string yaml = """
                            ansible:
                              install_method: pip
                              ansible_config: /etc/ansible/ansible.cfg
                              pull:
                                url: https://github.com/example/playbooks
                                playbook_name: site.yml
                                accept_host_key: true
                                clean: false
                            """;

        var config = CloudConfigYamlSerializer.Deserialize(yaml);

        config.Ansible.Should().NotBeNull();
        config.Ansible!.InstallMethod.Should().Be("pip");
        config.Ansible.AnsibleConfigPath.Should().Be("/etc/ansible/ansible.cfg");
        config.Ansible.Pull.Should().NotBeNull();
        config.Ansible.Pull!.Url.Should().Be("https://github.com/example/playbooks");
        config.Ansible.Pull.PlaybookName.Should().Be("site.yml");
        config.Ansible.Pull.AcceptHostKey.Should().BeTrue();
        config.Ansible.Pull.Clean.Should().BeFalse();
    }

    [Fact]
    public void DiskSetup_and_FsSetup_deserialise_to_typed_shapes()
    {
        const string yaml = """
                            disk_setup:
                              /dev/sdb:
                                table_type: gpt
                                overwrite: true
                            fs_setup:
                              - device: /dev/sdb1
                                filesystem: ext4
                                label: data
                                overwrite: true
                            """;

        var config = CloudConfigYamlSerializer.Deserialize(yaml);

        config.DiskSetup.Should().NotBeNull().And.ContainKey("/dev/sdb");
        config.DiskSetup!["/dev/sdb"].TableType.Should().Be("gpt");
        config.DiskSetup["/dev/sdb"].Overwrite.Should().BeTrue();

        config.FsSetup.Should().ContainSingle();
        config.FsSetup![0].Device.Should().Be("/dev/sdb1");
        config.FsSetup[0].Filesystem.Should().Be("ext4");
        config.FsSetup[0].Label.Should().Be("data");
    }

    [Fact]
    public void Reporting_with_webhook_handler_deserialises()
    {
        const string yaml = """
                            reporting:
                              webhook:
                                type: webhook
                                endpoint: https://example.com/hook
                                timeout: 10
                                retries: 3
                            """;

        var config = CloudConfigYamlSerializer.Deserialize(yaml);

        config.Reporting.Should().NotBeNull().And.ContainKey("webhook");
        config.Reporting!["webhook"].Type.Should().Be("webhook");
        config.Reporting["webhook"].Endpoint.Should().Be("https://example.com/hook");
        config.Reporting["webhook"].Timeout.Should().Be(10);
        config.Reporting["webhook"].Retries.Should().Be(3);
    }

    [Fact]
    public void SaltMinion_with_opaque_conf_keeps_structure()
    {
        // Salt's conf is opaque pass-through (IReadOnlyDictionary<string, object?>)
        // so nested mappings survive without us tracking every salt config key.
        const string yaml = """
                            salt_minion:
                              pkg_name: salt-minion
                              conf:
                                master: salt.example.com
                                id: web-01
                            """;

        var config = CloudConfigYamlSerializer.Deserialize(yaml);

        config.SaltMinion.Should().NotBeNull();
        config.SaltMinion!.PkgName.Should().Be("salt-minion");
        config.SaltMinion.Conf.Should().NotBeNull().And.ContainKey("master");
        config.SaltMinion.Conf!["master"].Should().Be("salt.example.com");
    }
}
