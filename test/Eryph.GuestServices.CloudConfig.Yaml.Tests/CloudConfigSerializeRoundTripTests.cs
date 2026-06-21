using AwesomeAssertions;
using Eryph.GuestServices.CloudConfig;
using Eryph.GuestServices.CloudConfig.Yaml;

namespace Eryph.GuestServices.CloudConfig.Yaml.Tests;

/// <summary>
/// Coverage for the model → YAML serialize path added so eryph can pre-merge
/// cloud-config fodder into a single document. The contract is round-trip
/// stability: serialising a parsed cloud-config and parsing the result again
/// yields an equivalent model, and the output is a valid <c>#cloud-config</c>
/// document.
/// </summary>
public sealed class CloudConfigSerializeRoundTripTests
{
    private static CloudConfig RoundTrip(string yaml)
    {
        var model = CloudConfigYamlSerializer.Deserialize(yaml);
        var serialized = CloudConfigYamlSerializer.Serialize(model);
        return CloudConfigYamlSerializer.Deserialize(serialized);
    }

    [Fact]
    public void Output_starts_with_cloud_config_header()
    {
        var model = CloudConfigYamlSerializer.Deserialize("hostname: web01");

        var serialized = CloudConfigYamlSerializer.Serialize(model);

        serialized.Should().StartWith("#cloud-config\n");
    }

    [Fact]
    public void Unset_members_are_omitted()
    {
        var model = CloudConfigYamlSerializer.Deserialize("hostname: web01");

        var serialized = CloudConfigYamlSerializer.Serialize(model);

        serialized.Should().Contain("hostname: web01");
        // A non-nullable Empty BoolOrString must not leak into the output.
        serialized.Should().NotContain("manage_etc_hosts");
        serialized.Should().NotContain("resize_rootfs");
        serialized.Should().NotContain("runcmd");
    }

    [Fact]
    public void Scalar_and_list_keys_round_trip()
    {
        const string yaml = """
                            hostname: web01
                            fqdn: web01.example.com
                            ssh_authorized_keys:
                              - ssh-ed25519 AAAAkey1 a@h
                              - ssh-ed25519 AAAAkey2 b@h
                            """;

        var result = RoundTrip(yaml);

        result.Hostname.Should().Be("web01");
        result.Fqdn.Should().Be("web01.example.com");
        result.SshAuthorizedKeys.Should().Equal(
            "ssh-ed25519 AAAAkey1 a@h", "ssh-ed25519 AAAAkey2 b@h");
    }

    [Fact]
    public void Runcmd_shell_and_argv_forms_round_trip()
    {
        const string yaml = """
                            runcmd:
                              - echo hello
                              - [ls, -l, /tmp]
                            """;

        var result = RoundTrip(yaml);

        result.Runcmd.Should().HaveCount(2);
        result.Runcmd![0].IsShellCommand.Should().BeTrue();
        result.Runcmd[0].Command.Should().Be("echo hello");
        result.Runcmd[1].IsShellCommand.Should().BeFalse();
        result.Runcmd[1].Argv.Should().Equal("ls", "-l", "/tmp");
    }

    [Fact]
    public void Bool_or_string_union_round_trips_both_variants()
    {
        // manage_etc_hosts is a plain bool token; resize_rootfs the string
        // "noblock" — the quoting must survive the round-trip so the string
        // variant does not collapse into a bool.
        const string yaml = """
                            manage_etc_hosts: true
                            resize_rootfs: noblock
                            """;

        var result = RoundTrip(yaml);

        result.ManageEtcHosts.IsBool.Should().BeTrue();
        result.ManageEtcHosts.Bool.Should().BeTrue();
        result.ResizeRootfs.IsString.Should().BeTrue();
        result.ResizeRootfs.String.Should().Be("noblock");
    }

    [Fact]
    public void String_variant_that_looks_like_a_bool_token_stays_a_string()
    {
        // A quoted "yes" is a string per PyYAML semantics; serialising it must
        // re-quote so it is not read back as a Bool.
        var model = new CloudConfig { ManageEtcHosts = BoolOrString.FromString("yes") };

        var serialized = CloudConfigYamlSerializer.Serialize(model);
        var result = CloudConfigYamlSerializer.Deserialize(serialized);

        result.ManageEtcHosts.IsString.Should().BeTrue();
        result.ManageEtcHosts.String.Should().Be("yes");
    }

    [Fact]
    public void Users_round_trip_by_name_with_keys()
    {
        const string yaml = """
                            users:
                              - name: admin
                                groups: [sudo, docker]
                                ssh_authorized_keys:
                                  - ssh-ed25519 AAAAkey admin@host
                                lock_passwd: false
                            """;

        var result = RoundTrip(yaml);

        result.Users.Should().ContainSingle();
        var user = result.Users![0];
        user.Name.Should().Be("admin");
        user.Groups.Should().Equal("sudo", "docker");
        user.SshAuthorizedKeys.Should().Equal("ssh-ed25519 AAAAkey admin@host");
        user.LockPasswd.Should().BeFalse();
    }

    [Fact]
    public void Write_files_with_permissions_round_trip()
    {
        const string yaml = """
                            write_files:
                              - path: /etc/app.conf
                                content: key=value
                                permissions: '0644'
                                owner: root:root
                            """;

        var result = RoundTrip(yaml);

        result.WriteFiles.Should().ContainSingle();
        var file = result.WriteFiles![0];
        file.Path.Should().Be("/etc/app.conf");
        file.Content.Should().Be("key=value");
        file.Permissions.Should().Be("0644");
        file.Owner.Should().Be("root:root");
    }

    [Fact]
    public void Aliased_keys_emit_cloud_init_spelling()
    {
        var model = CloudConfigYamlSerializer.Deserialize("ssh_deletekeys: false");

        var serialized = CloudConfigYamlSerializer.Serialize(model);

        // Must emit the cloud-init key spelling, not the property-derived
        // ssh_delete_keys.
        serialized.Should().Contain("ssh_deletekeys");
        serialized.Should().NotContain("ssh_delete_keys");
    }
}
