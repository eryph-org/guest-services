using AwesomeAssertions;
using Eryph.GuestServices.CloudConfig;
using Eryph.GuestServices.Provisioning.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Eryph.GuestServices.Provisioning.Tests.Serialization;

public sealed class CloudConfigSerializerTests
{
    // ---- Acknowledged-but-no-op keys (Info) ----

    [Fact]
    public void Apt_block_logs_at_Info_with_explanation()
    {
        var captured = new CapturingLogger<CloudConfigSerializer>();
        var serializer = new CloudConfigSerializer(captured);
        // Phase 2 typed apt.sources as a dict of AptSourceEntry — each
        // value is itself a mapping (source / keyid / etc.) per cloud-
        // init's documented schema.
        const string yaml = """
            #cloud-config
            apt:
              sources:
                main:
                  source: 'deb http://archive.ubuntu.com/ubuntu jammy main'
            """;

        var result = serializer.Deserialize(yaml);

        result.Apt.Should().NotBeNull();
        captured.Records.Should().ContainSingle(r =>
            r.Level == LogLevel.Information
            && r.Message.Contains("apt")
            && r.Message.Contains("not applied"));
    }

    [Fact]
    public void Multiple_linux_keys_each_log_one_Info_line()
    {
        // Cross-cloud catlets routinely carry apt + packages + snap together;
        // each acknowledged key surfaces its own log line so the operator
        // sees the full inventory of "we saw this, we did nothing".
        var captured = new CapturingLogger<CloudConfigSerializer>();
        var serializer = new CloudConfigSerializer(captured);
        const string yaml = """
            #cloud-config
            apt:
              proxy: http://proxy:8080
            snap:
              commands:
                - snap install foo
            packages:
              - git
              - vim
            """;

        serializer.Deserialize(yaml);

        var infos = captured.Records.Where(r => r.Level == LogLevel.Information).ToList();
        infos.Should().HaveCount(3);
        infos.Should().Contain(r => r.Message.Contains("apt"));
        infos.Should().Contain(r => r.Message.Contains("snap"));
        infos.Should().Contain(r => r.Message.Contains("packages"));
    }

    [Fact]
    public void No_acknowledged_keys_present_emits_no_Info_noise()
    {
        // Most Windows-only catlets carry no Linux keys. The Info path must
        // stay silent in that case — operators reading the log shouldn't see
        // dead "nothing to report" lines.
        var captured = new CapturingLogger<CloudConfigSerializer>();
        var serializer = new CloudConfigSerializer(captured);
        const string yaml = """
            #cloud-config
            hostname: win-host
            """;

        serializer.Deserialize(yaml);

        captured.Records.Where(r => r.Level == LogLevel.Information).Should().BeEmpty();
    }

    [Fact]
    public void Bool_flag_explicitly_false_still_surfaces_at_Info()
    {
        // package_update: false is an explicit operator decision — "don't
        // update" — and we want them to know we didn't act on either branch.
        // Distinct from "key omitted entirely" which logs nothing.
        var captured = new CapturingLogger<CloudConfigSerializer>();
        var serializer = new CloudConfigSerializer(captured);
        const string yaml = """
            #cloud-config
            package_update: false
            """;

        serializer.Deserialize(yaml);

        captured.Records.Should().Contain(r =>
            r.Level == LogLevel.Information && r.Message.Contains("package_update"));
    }

    // ---- Truly unknown keys (Warning) ----

    [Fact]
    public void Genuinely_unknown_key_logs_at_Warning()
    {
        // Typo or undocumented extension — schema does not list it, so the
        // YAML deserializer's onUnknownKey callback fires Warning.
        var captured = new CapturingLogger<CloudConfigSerializer>();
        var serializer = new CloudConfigSerializer(captured);
        const string yaml = """
            #cloud-config
            hsotname: typo
            """;

        serializer.Deserialize(yaml);

        captured.Records.Should().Contain(r =>
            r.Level == LogLevel.Warning && r.Message.Contains("hsotname"));
    }

    [Fact]
    public void Every_new_phase2_linux_key_surfaces_at_Info()
    {
        // Single fixture exercising every Phase 2 Linux-only key so the
        // inventory-driven Info logging covers them. Any new acknowledged-
        // but-no-op key must either appear here (and produce an Info line)
        // or carry CloudInitPlatforms.All on the model — otherwise the
        // inventory dropped a property and operators won't see it.
        var captured = new CapturingLogger<CloudConfigSerializer>();
        var serializer = new CloudConfigSerializer(captured);
        const string yaml = """
            #cloud-config
            apt:
              proxy: http://proxy:8080
            apt_pipelining: default
            packages:
              - git
            package_update: true
            package_upgrade: true
            package_reboot_if_required: true
            snap:
              commands: ['snap install hello']
            yum_repos:
              epel:
                baseurl: https://example/epel
            yum_repo_dir: /etc/yum.repos.d
            disk_setup:
              /dev/sdb:
                table_type: gpt
            fs_setup:
              - device: /dev/sdb1
                filesystem: ext4
            mounts:
              - [/dev/sdb1, /data, ext4, defaults, '0', '2']
            mount_default_fields: [none, none, auto, defaults, '0', '2']
            swap:
              filename: /swap.img
              size: 1G
            manage_etc_hosts: localhost
            manage_resolv_conf: true
            resolv_conf:
              nameservers: [1.1.1.1]
            bootcmd:
              - echo boot
            phone_home:
              url: https://hook
            final_message: done
            ca_certs:
              remove_defaults: true
            disable_root: true
            disable_root_opts: 'no-port-forwarding'
            chef:
              server_url: https://chef
            ansible:
              install_method: pip
            puppet:
              install: true
            salt_minion:
              pkg_name: salt-minion
            disable_ec2_metadata: true
            migrate: false
            ssh_deletekeys: true
            ssh_genkeytypes: [ed25519]
            ssh_import_id: gh:octocat
            ssh_keys:
              rsa_private: 'X'
            ssh_publish_hostkeys:
              enabled: true
            byobu_by_default: system
            resize_rootfs: noblock
            locale_configfile: /etc/default/locale
            random_seed:
              file: /dev/urandom
              data: seed
            output:
              all: '| tee'
            reporting:
              webhook:
                type: webhook
            update_etc_hosts: true
            update_hostname: true
            """;

        serializer.Deserialize(yaml);

        // Every Linux-only inventory entry currently present in the YAML
        // must produce its own Info line. The count comes from the
        // source-generated inventory so this test stays in lockstep with
        // the model.
        var linuxFields = CloudConfigPlatformInventory.Fields
            .Where(f => f.Platforms == CloudInitPlatforms.Linux)
            .ToList();
        var infos = captured.Records.Where(r => r.Level == LogLevel.Information).ToList();
        infos.Should().HaveCount(linuxFields.Count,
            "every CloudInitPlatforms.Linux field in the inventory must surface " +
            "exactly one Info line for this all-keys-set fixture. " +
            "If this count drifts, either the fixture lost a key or a model " +
            "field is no longer being deserialised correctly.");
        captured.Records.Should().NotContain(r => r.Level == LogLevel.Warning,
            "the fixture should parse cleanly without any unknown-key warnings");
    }

    [Fact]
    public void Implemented_module_keys_emit_no_Info_or_Warning_noise()
    {
        // hostname is a real implemented module; it should parse silently
        // (no Warning, no Info). The tiering is: implemented → silent;
        // acknowledged-no-op → Info; unknown → Warning. This pins tier 1.
        var captured = new CapturingLogger<CloudConfigSerializer>();
        var serializer = new CloudConfigSerializer(captured);
        const string yaml = """
            #cloud-config
            hostname: real-host
            timezone: UTC
            ntp:
              enabled: true
            """;

        serializer.Deserialize(yaml);

        captured.Records.Should().BeEmpty();
    }

    // ---- Test helper: capturing logger ----

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Records { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull =>
            NullLogger.Instance.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Records.Add((logLevel, formatter(state, exception)));
        }
    }
}
