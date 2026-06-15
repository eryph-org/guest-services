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
    public void Deserialize_EgsBlock_ParsesSettingsAndUpdate()
    {
        const string yaml = """
                            egs:
                              settings:
                                remote_access: false
                                provisioning: true
                                kvp_auth: false
                              update:
                                enabled: true
                                version: "0.4.0"
                                channel: stable
                            """;

        var config = CloudConfigYamlSerializer.Deserialize(yaml);

        config.Egs.Should().NotBeNull();
        config.Egs!.Settings.Should().NotBeNull();
        config.Egs.Settings!.RemoteAccess.Should().BeFalse();
        config.Egs.Settings.Provisioning.Should().BeTrue();
        config.Egs.Settings.KvpAuth.Should().BeFalse();
        config.Egs.Update.Should().NotBeNull();
        config.Egs.Update!.Enabled.Should().BeTrue();
        config.Egs.Update.Version.Should().Be("0.4.0");
        config.Egs.Update.Channel.Should().Be("stable");
    }

    [Fact]
    public void Deserialize_EgsSettings_OmittedSwitchesStayNull()
    {
        // Three-state: an omitted switch must remain null (leave untouched),
        // distinct from an explicit false.
        const string yaml = """
                            egs:
                              settings:
                                remote_access: false
                            """;

        var config = CloudConfigYamlSerializer.Deserialize(yaml);

        config.Egs!.Settings!.RemoteAccess.Should().BeFalse();
        config.Egs.Settings.Provisioning.Should().BeNull();
        config.Egs.Settings.KvpAuth.Should().BeNull();
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

    // Regression: discovered via Pester sample 03 — an unquoted scalar list item
    // that contains ': ' is a YAML mapping indicator, not a shell string. The
    // user MUST single- or double-quote the whole scalar. We document both the
    // failing form (parse error) and the working form (parses as shell command).
    [Fact]
    public void Deserialize_RuncmdScalarWithUnquotedColonSpace_Throws()
    {
        const string yaml = """
                            runcmd:
                              - echo "first: with colon"
                            """;

        var act = () => CloudConfigYamlSerializer.Deserialize(yaml);

        act.Should().Throw<Eryph.ConfigModel.InvalidConfigException>();
    }

    [Fact]
    public void Deserialize_RuncmdSingleQuotedScalarWithColon_ParsesAsShellCommand()
    {
        const string yaml = """
                            runcmd:
                              - 'echo "first: with colon"'
                            """;

        var config = CloudConfigYamlSerializer.Deserialize(yaml);

        config.Runcmd.Should().ContainSingle().Which.Should().Match<RuncmdEntry>(
            e => e.IsShellCommand && e.Command == "echo \"first: with colon\"" && e.Argv == null);
    }

    // cloudbase-init compat: argv-form runcmd entries can have YAML-quoted
    // tokens. The quoting is transparent — same RuncmdEntry shape as unquoted
    // tokens. Documenting because the user flagged it explicitly.
    [Fact]
    public void Deserialize_RuncmdArgvWithQuotedTokens_ParsesAsArgv()
    {
        const string yaml = """
                            runcmd:
                              - [ "powershell.exe", "-NoProfile", "-Command", "Write-Host hi" ]
                            """;

        var config = CloudConfigYamlSerializer.Deserialize(yaml);

        config.Runcmd.Should().ContainSingle().Which.Should().Match<RuncmdEntry>(
            e => !e.IsShellCommand && e.Argv != null
                 && e.Argv.Count == 4
                 && e.Argv[0] == "powershell.exe"
                 && e.Argv[1] == "-NoProfile"
                 && e.Argv[2] == "-Command"
                 && e.Argv[3] == "Write-Host hi");
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
    public void Deserialize_UserWithPlainTextPasswd_MapsField()
    {
        const string yaml = """
                            users:
                              - name: admin
                                plain_text_passwd: secret
                            """;

        var config = CloudConfigYamlSerializer.Deserialize(yaml);

        config.Users.Should().ContainSingle().Which.Should().Match<UserConfig>(
            u => u.Name == "admin" && u.PlainTextPasswd == "secret" && u.Passwd == null);
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
    public void Deserialize_WriteFilePermissionsWithHexPrefix_Throws()
    {
        const string yaml = """
                            write_files:
                            - path: /tmp/x
                              content: hi
                              permissions: "0x644"
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
    public void Deserialize_OnlyHeader_ReturnsEmpty()
    {
        const string yaml = "#cloud-config\n";

        var config = CloudConfigYamlSerializer.Deserialize(yaml);

        config.Should().NotBeNull();
        config.Hostname.Should().BeNull();
        config.Users.Should().BeNull();
        config.WriteFiles.Should().BeNull();
        config.Runcmd.Should().BeNull();
    }

    [Fact]
    public void Deserialize_EmptyString_ReturnsEmpty()
    {
        var config = CloudConfigYamlSerializer.Deserialize(string.Empty);

        config.Should().NotBeNull();
        config.Hostname.Should().BeNull();
    }

    [Fact]
    public void Deserialize_WriteFilesExplicitNull_ReturnsNullProperty()
    {
        const string yaml = "write_files: ~";

        var config = CloudConfigYamlSerializer.Deserialize(yaml);

        config.WriteFiles.Should().BeNull();
    }

    [Fact]
    public void Deserialize_UsersWithEmptyValue_ReturnsNullProperty()
    {
        const string yaml = "users:";

        var config = CloudConfigYamlSerializer.Deserialize(yaml);

        config.Users.Should().BeNull();
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
    public void Deserialize_UnknownProperty_does_not_throw_and_surfaces_via_callback()
    {
        // Cloud-init's runtime behaviour for unknown cloud-config keys is
        // "warn, don't fail" (cloudinit/config/schema.py validate_cloudconfig_schema).
        // We mirror that: deserialization succeeds, and a caller-supplied
        // callback receives every unknown top-level key so the wrapping
        // service can log at Warning level. Strict-mode checking belongs
        // in the `validate` subcommand, not the runtime parser.
        const string yaml = "unknown_key: value\nhostname: still-parses";
        var unknown = new List<string>();

        var config = CloudConfigYamlSerializer.Deserialize(yaml, onUnknownKey: unknown.Add);

        config.Hostname.Should().Be("still-parses");
        unknown.Should().BeEquivalentTo("unknown_key");
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
                second.Sudo.Should().Equal("ALL=(ALL) NOPASSWD:ALL");
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

    [Fact]
    public void Deserialize_growpart_with_mode_and_devices_roundtrips()
    {
        // Real-world fixture shape — verbatim from the cloud-init docs for
        // cc_growpart, transliterated to the Windows-friendly device list.
        // Drive letters with a trailing colon MUST be quoted: in YAML 1.2,
        // a bare `- D:` is parsed as a mapping with key "D" and null value,
        // not as the scalar string "D:". This test pins the documented shape.
        const string yaml = """
                            growpart:
                              mode: auto
                              devices:
                                - /
                                - "D:"
                                - all
                            """;

        var config = CloudConfigYamlSerializer.Deserialize(yaml);

        config.Growpart.Should().NotBeNull();
        config.Growpart!.Mode.Should().Be("auto");
        config.Growpart.Devices.Should().Equal("/", "D:", "all");
    }

    [Fact]
    public void Deserialize_growpart_devices_accept_bare_drive_letter_without_colon()
    {
        // A no-colon shorthand is the friendliest form for cloud-config
        // authors who didn't know to quote `D:` — the GrowpartModule accepts
        // a bare letter as the same drive.
        const string yaml = """
                            growpart:
                              devices:
                                - C
                                - D
                            """;

        var config = CloudConfigYamlSerializer.Deserialize(yaml);

        config.Growpart.Should().NotBeNull();
        config.Growpart!.Devices.Should().Equal("C", "D");
    }

    [Fact]
    public void Deserialize_growpart_off_disables_the_module()
    {
        const string yaml = """
                            growpart:
                              mode: off
                            """;

        var config = CloudConfigYamlSerializer.Deserialize(yaml);

        // YAML 1.2 keeps `off` as a string; the GrowpartModule treats it
        // (and the literal `"false"` string) as the disable signal.
        config.Growpart.Should().NotBeNull();
        config.Growpart!.Mode.Should().Be("off");
        config.Growpart.Devices.Should().BeNull();
    }

    [Fact]
    public void Deserialize_growpart_mode_unquoted_boolean_false_disables_the_module()
    {
        // cloud-init documents `mode: false` (unquoted YAML boolean) as a
        // valid way to disable growpart, alongside `mode: off` (string) and
        // `mode: "false"` (quoted string). YamlDotNet's bool→string coercion
        // for a string-typed field MUST land as "false" (or the module's
        // case-insensitive "false"/"off" check would silently fall through
        // to `mode: auto` — the opposite of the operator's intent).
        const string yaml = """
                            growpart:
                              mode: false
                            """;

        var config = CloudConfigYamlSerializer.Deserialize(yaml);

        config.Growpart.Should().NotBeNull();
        // Case-insensitive match — YamlDotNet may emit "False" or "false"
        // depending on internal handling; either is fine for the module's
        // ToLowerInvariant() comparison.
        config.Growpart!.Mode.Should().BeOneOf("false", "False");
    }

    [Fact]
    public void Deserialize_growpart_absent_yields_null()
    {
        const string yaml = "hostname: nogrow";

        var config = CloudConfigYamlSerializer.Deserialize(yaml);

        config.Growpart.Should().BeNull();
    }

    [Fact]
    public void Deserialize_ntp_with_servers_and_pools_roundtrips()
    {
        const string yaml = """
                            ntp:
                              enabled: true
                              servers:
                                - time.windows.com
                                - time.nist.gov
                              pools:
                                - pool.ntp.org
                            """;

        var config = CloudConfigYamlSerializer.Deserialize(yaml);

        config.Ntp.Should().NotBeNull();
        config.Ntp!.Enabled.Should().BeTrue();
        config.Ntp.Servers.Should().Equal("time.windows.com", "time.nist.gov");
        config.Ntp.Pools.Should().Equal("pool.ntp.org");
    }

    [Fact]
    public void Deserialize_timezone_string_roundtrips()
    {
        const string yaml = "timezone: Europe/Berlin";

        var config = CloudConfigYamlSerializer.Deserialize(yaml);

        config.Timezone.Should().Be("Europe/Berlin");
    }

    [Fact]
    public void Deserialize_locale_and_keyboard_roundtrip()
    {
        // Keyboard layouts that contain a colon must be quoted in YAML.
        const string yaml = """
                            locale: de-DE
                            keyboard:
                              layout: "0407:00000407"
                            """;

        var config = CloudConfigYamlSerializer.Deserialize(yaml);

        config.Locale.Should().Be("de-DE");
        config.Keyboard.Should().NotBeNull();
        config.Keyboard!.Layout.Should().Be("0407:00000407");
    }

    [Fact]
    public void Deserialize_license_block_roundtrips()
    {
        const string yaml = """
                            license:
                              product_key: ABCDE-FGHIJ-KLMNO-PQRST-UVWXY
                              kms_host: "kms.example.com:1688"
                              activate: true
                            """;

        var config = CloudConfigYamlSerializer.Deserialize(yaml);

        config.License.Should().NotBeNull();
        config.License!.ProductKey.Should().Be("ABCDE-FGHIJ-KLMNO-PQRST-UVWXY");
        config.License.KmsHost.Should().Be("kms.example.com:1688");
        config.License.Activate.Should().BeTrue();
    }

    [Fact]
    public void Deserialize_power_state_block_roundtrips()
    {
        const string yaml = """
                            power_state:
                              mode: reboot
                              delay: '+5'
                              message: 'Provisioning complete'
                              timeout: 30
                              condition: true
                            """;

        var config = CloudConfigYamlSerializer.Deserialize(yaml);

        config.PowerState.Should().NotBeNull();
        config.PowerState!.Mode.Should().Be("reboot");
        config.PowerState.Delay.Should().Be("+5");
        config.PowerState.Message.Should().Be("Provisioning complete");
        config.PowerState.Timeout.Should().Be(30);
        // Plain (unquoted) `true` → bool variant of BoolOrString.
        // BoolOrStringYamlConverter restores the PyYAML / cloud-init
        // behaviour that YamlDotNet's default object?-target
        // deserialisation would otherwise lose.
        config.PowerState.Condition.IsBool.Should().BeTrue();
        config.PowerState.Condition.Bool.Should().Be(true);
    }

    [Fact]
    public void Deserialize_power_state_quoted_true_stays_a_string()
    {
        // Cloud-init parity edge case: `condition: "true"` (quoted) is a
        // shell command (`true` on Linux exits 0). It must land as string,
        // not bool, so the module dispatches via RunShellCommandAsync.
        const string yaml = """
                            power_state:
                              condition: "true"
                            """;

        var config = CloudConfigYamlSerializer.Deserialize(yaml);

        config.PowerState!.Condition.IsString.Should().BeTrue();
        config.PowerState.Condition.String.Should().Be("true");
    }

    [Fact]
    public void Deserialize_power_state_plain_false_is_native_bool()
    {
        const string yaml = """
                            power_state:
                              condition: false
                            """;

        var config = CloudConfigYamlSerializer.Deserialize(yaml);

        config.PowerState!.Condition.IsBool.Should().BeTrue();
        config.PowerState.Condition.Bool.Should().Be(false);
    }

    [Fact]
    public void Deserialize_power_state_condition_string_roundtrips()
    {
        // Cloud-init shape: condition may be a bool literal OR a shell
        // command string. The POCO field is now BoolOrString; we need
        // YamlDotNet to populate the right variant.
        const string yaml = """
                            power_state:
                              mode: poweroff
                              condition: 'exit 0'
                            """;

        var config = CloudConfigYamlSerializer.Deserialize(yaml);

        config.PowerState.Should().NotBeNull();
        config.PowerState!.Condition.IsString.Should().BeTrue();
        config.PowerState.Condition.String.Should().Be("exit 0");
    }

    [Fact]
    public void Deserialize_user_block_with_full_cross_platform_schema_roundtrips()
    {
        // Phase 2A schema expansion regression. Pins:
        //   - gecos (cross-platform; maps to Windows NTUser FullName)
        //   - ssh_import_id scalar form (auto-promoted to a one-element list)
        //   - sudo list form (cloud-init's documented multi-line shape)
        //   - expiredate / uid / no_create_home (Linux-only fields)
        // round-tripping cleanly through the user-converter.
        const string yaml = """
                            users:
                            - name: alice
                              gecos: Alice Wonderland
                              ssh_import_id: gh:alice
                              sudo:
                              - ALL=(ALL) NOPASSWD:ALL
                              - "Defaults: alice !requiretty"
                              expiredate: '2099-12-31'
                              uid: 4242
                              no_create_home: true
                            """;

        var config = CloudConfigYamlSerializer.Deserialize(yaml);

        config.Users.Should().ContainSingle().Which.Should().Match<UserConfig>(u =>
            u.Name == "alice"
            && u.Gecos == "Alice Wonderland"
            && u.Expiredate == "2099-12-31"
            && u.Uid == 4242
            && u.NoCreateHome == true);
        var alice = config.Users![0];
        alice.SshImportId.Should().Equal("gh:alice");
        alice.Sudo.Should().Equal("ALL=(ALL) NOPASSWD:ALL", "Defaults: alice !requiretty");
    }

    [Fact]
    public void Deserialize_user_sudo_as_scalar_promotes_to_single_element_list()
    {
        // The cloud-init schema documents both forms — single scalar and
        // sequence. The user-converter's IsStringListShorthandProperty
        // teaches the converter to promote a scalar to a one-element list,
        // matching the existing behaviour for ssh_authorized_keys / groups.
        const string yaml = """
                            users:
                            - name: alice
                              sudo: ALL=(ALL) NOPASSWD:ALL
                            """;

        var config = CloudConfigYamlSerializer.Deserialize(yaml);

        config.Users.Should().ContainSingle().Which.Sudo
            .Should().Equal("ALL=(ALL) NOPASSWD:ALL");
    }

    [Fact]
    public void Deserialize_user_ssh_import_id_as_list_keeps_list()
    {
        const string yaml = """
                            users:
                            - name: alice
                              ssh_import_id:
                              - gh:alice
                              - lp:alice
                            """;

        var config = CloudConfigYamlSerializer.Deserialize(yaml);

        config.Users.Should().ContainSingle().Which.SshImportId
            .Should().Equal("gh:alice", "lp:alice");
    }

    [Fact]
    public void Deserialize_user_unknown_property_does_not_throw()
    {
        // Mirrors the root-level IgnoreUnmatchedProperties policy — unknown
        // user-level keys (vendor extensions, typos, future cloud-init
        // additions) must NOT raise. The known keys land normally.
        const string yaml = """
                            users:
                            - name: alice
                              not_a_real_user_field: should_be_silently_ignored
                              shell: /bin/bash
                            """;

        var config = CloudConfigYamlSerializer.Deserialize(yaml);

        config.Users.Should().ContainSingle().Which.Should().Match<UserConfig>(u =>
            u.Name == "alice" && u.Shell == "/bin/bash");
    }

    [Fact]
    public void Deserialize_user_unknown_property_with_complex_value_drains_cleanly()
    {
        // Unknown user-level properties may carry mappings or sequences;
        // the converter must drain the entire value via the root deserializer
        // so the parser advances past it. This pins the "well-formed cloud-
        // config with an unknown nested block beside a known field" case.
        const string yaml = """
                            users:
                            - name: alice
                              vendor_block:
                                key1: foo
                                key2:
                                  - a
                                  - b
                              shell: /bin/bash
                            """;

        var config = CloudConfigYamlSerializer.Deserialize(yaml);

        config.Users.Should().ContainSingle().Which.Should().Match<UserConfig>(u =>
            u.Name == "alice" && u.Shell == "/bin/bash");
    }

    [Fact]
    public void Deserialize_write_files_defer_roundtrips()
    {
        // `defer: true` postpones the write until the Final stage so the file
        // can reference users that earlier modules in the same run created.
        // Phase 3 wires the runtime semantics — for now the field round-trips
        // through the schema.
        const string yaml = """
                            write_files:
                            - path: /home/alice/.bashrc
                              content: "alias ll='ls -la'\n"
                              defer: true
                            """;

        var config = CloudConfigYamlSerializer.Deserialize(yaml);

        config.WriteFiles.Should().ContainSingle().Which.Should().Match<WriteFileConfig>(w =>
            w.Path == "/home/alice/.bashrc" && w.Defer == true);
    }

    [Fact]
    public void Deserialize_chpasswd_expire_and_list_form_roundtrip()
    {
        // The legacy `list:` form is the newline-separated user:password
        // payload cloud-init still accepts as of 24.x. `expire: true` is
        // also part of the cross-platform schema — Phase 3 wires the
        // "expire on next login" semantics.
        const string yaml = """
                            chpasswd:
                              expire: true
                              list: |
                                admin:s3cret
                                alice:RANDOM
                            """;

        var config = CloudConfigYamlSerializer.Deserialize(yaml);

        config.Chpasswd.Should().NotBeNull();
        config.Chpasswd!.Expire.Should().BeTrue();
        // YamlDotNet's `|` block-scalar handling trims the trailing newline
        // even though YAML 1.2 clip-chomp keeps one — for our purposes the
        // payload (the user:password pairs) is what matters.
        config.Chpasswd.List.Should().NotBeNull();
        config.Chpasswd.List!.TrimEnd('\n').Should().Be("admin:s3cret\nalice:RANDOM");
    }

    [Fact]
    public void Deserialize_keyboard_with_model_variant_options_roundtrips()
    {
        // Cloud-init's cc_keyboard schema documents model/variant/options for
        // the X11 case. They're Linux-only at runtime but round-trip on
        // Windows so cross-cloud cloud-config doesn't lose data.
        const string yaml = """
                            keyboard:
                              layout: us
                              model: pc105
                              variant: dvorak
                              options: ctrl:nocaps
                            """;

        var config = CloudConfigYamlSerializer.Deserialize(yaml);

        config.Keyboard.Should().NotBeNull();
        config.Keyboard!.Layout.Should().Be("us");
        config.Keyboard.Model.Should().Be("pc105");
        config.Keyboard.Variant.Should().Be("dvorak");
        config.Keyboard.Options.Should().Be("ctrl:nocaps");
    }

    [Fact]
    public void Deserialize_groups_object_form_with_gid_roundtrips()
    {
        // Cloud-init's groups schema accepts either `name: [members]` or the
        // object form `name: {members: [...], gid: int}` for pinning the gid.
        const string yaml = """
                            groups:
                            - name: devs
                              members: [alice, bob]
                              gid: 5000
                            """;

        var config = CloudConfigYamlSerializer.Deserialize(yaml);

        config.Groups.Should().ContainSingle().Which.Should().Match<GroupConfig>(g =>
            g.Name == "devs" && g.Gid == 5000);
        config.Groups![0].Members.Should().Equal("alice", "bob");
    }

    [Fact]
    public void Deserialize_license_extended_flags_roundtrip()
    {
        // Pin the new automation flags (set_avma / set_kms / rearm / force)
        // so renames or YAML naming-convention drift surface immediately.
        const string yaml = """
                            license:
                              set_avma: true
                              set_kms: false
                              rearm: true
                              force: true
                            """;

        var config = CloudConfigYamlSerializer.Deserialize(yaml);

        config.License.Should().NotBeNull();
        config.License!.SetAvma.Should().BeTrue();
        config.License.SetKms.Should().BeFalse();
        config.License.Rearm.Should().BeTrue();
        config.License.Force.Should().BeTrue();
    }
}
