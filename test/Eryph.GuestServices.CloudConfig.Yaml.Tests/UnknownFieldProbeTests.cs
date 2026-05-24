using AwesomeAssertions;
using Eryph.GuestServices.CloudConfig.Yaml;

namespace Eryph.GuestServices.CloudConfig.Yaml.Tests;

/// <summary>
/// Cloud-init's runtime behaviour for unknown / additional cloud-config
/// keys is "log a warning, continue processing" (validate_cloudconfig_schema
/// in cloud-init source). We mirror that:
/// <list type="bullet">
///   <item>Deserialization NEVER throws on unknown keys — cross-cloud YAML
///   with Linux-only keys (apt:, snap:, ntp_client:) must round-trip.</item>
///   <item>A caller-supplied callback is invoked once per unknown key so a
///   logging wrapper can surface them at Warning level.</item>
/// </list>
/// </summary>
public sealed class UnknownFieldProbeTests
{
    [Fact]
    public void Linux_only_ntp_field_does_not_throw()
    {
        const string yaml = """
                            ntp:
                              enabled: true
                              ntp_client: chrony
                              servers: [time.windows.com]
                            """;

        var config = CloudConfigYamlSerializer.Deserialize(yaml);

        config.Ntp.Should().NotBeNull();
        config.Ntp!.Servers.Should().Equal("time.windows.com");
    }

    [Fact]
    public void Unknown_top_level_field_does_not_throw()
    {
        // `not_a_real_cloud_init_key` is genuinely unknown — not in our
        // schema and not a documented cloud-init key. Must NOT throw.
        const string yaml = """
                            hostname: test
                            not_a_real_cloud_init_key:
                              whatever: foo
                            """;

        var config = CloudConfigYamlSerializer.Deserialize(yaml);

        config.Hostname.Should().Be("test");
    }

    [Fact]
    public void Truly_unknown_key_invokes_the_callback_once()
    {
        // The callback fires only for keys that are NOT on the CloudConfig
        // schema. Acknowledged Linux-only keys (apt, snap, …) have been
        // promoted to schema fields so they parse cleanly; CloudConfigSerializer
        // emits Info logs for them via its acknowledged-key inventory. The
        // callback path here is strictly the "I have no idea what this is"
        // channel for typos / undocumented vendor extensions.
        var seen = new List<string>();
        const string yaml = """
                            hostname: test
                            apt:
                              sources: {}
                            mystery_key:
                              foo: bar
                            another_mystery: 42
                            """;

        var config = CloudConfigYamlSerializer.Deserialize(yaml, onUnknownKey: seen.Add);

        config.Hostname.Should().Be("test");
        config.Apt.Should().NotBeNull();
        seen.Should().BeEquivalentTo("mystery_key", "another_mystery");
    }

    [Fact]
    public void Acknowledged_linux_keys_do_NOT_invoke_the_callback()
    {
        // These were "unknown" before the schema additions and would have
        // produced Warning-level noise on every cross-cloud catlet that
        // shipped a Linux apt/snap/packages block. Now they're recognised
        // schema fields; CloudConfigSerializer handles the Info-logging on
        // the consumer side.
        var seen = new List<string>();
        const string yaml = """
                            apt:
                              sources: {}
                            snap:
                              commands: []
                            packages: [git, vim]
                            yum_repos: {}
                            bootcmd: [echo hi]
                            power_state:
                              mode: reboot
                            phone_home:
                              url: http://example.com
                            disable_root: true
                            chef:
                              install_type: omnibus
                            """;

        CloudConfigYamlSerializer.Deserialize(yaml, onUnknownKey: seen.Add);

        seen.Should().BeEmpty();
    }

    [Fact]
    public void Known_top_level_keys_do_NOT_invoke_the_callback()
    {
        // Sanity check the inverse — keys that ARE on the POCO must not be
        // mis-reported as unknown.
        var seen = new List<string>();
        const string yaml = """
                            hostname: web01
                            ntp:
                              servers: [time.windows.com]
                            growpart:
                              mode: auto
                            license:
                              set_avma: true
                            """;

        var config = CloudConfigYamlSerializer.Deserialize(yaml, onUnknownKey: seen.Add);

        config.Hostname.Should().Be("web01");
        seen.Should().BeEmpty();
    }

    [Fact]
    public void Callback_is_optional()
    {
        // The single-arg overload (no callback) is the existing public API
        // and must keep working — unknown keys are still ignored silently
        // when the caller doesn't care.
        const string yaml = "hostname: x\nunknown_key: ignored";

        var config = CloudConfigYamlSerializer.Deserialize(yaml);

        config.Hostname.Should().Be("x");
    }

    [Fact]
    public void Object_typed_property_round_trips_arbitrary_shapes()
    {
        // Sanity: YamlDotNet must be able to deserialize a mapping into a
        // bare `object?` property without us defining a POCO. We rely on
        // this for the Linux-only acknowledged keys (Apt, YumRepos, …)
        // which have unbounded schemas we don't want to track.
        const string yaml = """
                            hostname: x
                            apt:
                              sources:
                                deb:
                                  key: stuff
                                  filename: src.list
                            """;

        var config = CloudConfigYamlSerializer.Deserialize(yaml);

        config.Hostname.Should().Be("x");
        config.Apt.Should().NotBeNull();
    }

    [Fact]
    public void ManageEtcHosts_plain_bool_resolves_to_bool()
    {
        // Cloud-init documents `manage_etc_hosts` as a bool | string union
        // (true / false / "localhost" / "template"). With BoolOrString
        // typing, a plain YAML 1.1 bool token resolves to bool — matching
        // PyYAML SafeLoader / cloud-init exactly. The downstream (Linux)
        // module sees a real bool, not a stringified literal.
        const string yaml = "manage_etc_hosts: true";

        var config = CloudConfigYamlSerializer.Deserialize(yaml);

        config.ManageEtcHosts.IsBool.Should().BeTrue();
        config.ManageEtcHosts.Bool.Should().Be(true);
    }

    [Fact]
    public void ManageEtcHosts_quoted_bool_keeps_string()
    {
        // Quoted scalars stay as strings even when the text is a bool token —
        // operator quoted intentionally. Preserves the PyYAML distinction.
        const string yaml = "manage_etc_hosts: \"true\"";

        var config = CloudConfigYamlSerializer.Deserialize(yaml);

        config.ManageEtcHosts.IsString.Should().BeTrue();
        config.ManageEtcHosts.String.Should().Be("true");
    }

    [Fact]
    public void ManageEtcHosts_string_enum_round_trips()
    {
        // The documented string literals (`localhost`, `template`) must
        // survive verbatim — these are the most-used values in real
        // cloud-config YAML.
        const string yaml = "manage_etc_hosts: localhost";

        var config = CloudConfigYamlSerializer.Deserialize(yaml);

        config.ManageEtcHosts.IsString.Should().BeTrue();
        config.ManageEtcHosts.String.Should().Be("localhost");
    }

    [Fact]
    public void Object_property_plain_integer_resolves_to_long()
    {
        // PyYAML resolves plain integer scalars natively; we follow suit
        // so a future cloud-config-typed numeric union doesn't need a
        // per-property converter.
        const string yaml = "apt_pipelining: 42";

        var config = CloudConfigYamlSerializer.Deserialize(yaml);

        config.AptPipelining.Should().Be(42L);
    }

    [Fact]
    public void Object_property_plain_null_resolves_to_null()
    {
        // YAML's null tokens (`~`, `null`, empty) all resolve to .NET null.
        // apt_pipelining stays `object?` because cloud-init accepts a union
        // of bool (true/false/none) plus the string "default" — the YAML 1.2
        // resolver dispatches each form to its native .NET type.
        const string yaml = "apt_pipelining: ~";

        var config = CloudConfigYamlSerializer.Deserialize(yaml);

        config.AptPipelining.Should().BeNull();
    }

    [Fact]
    public void Object_property_mapping_stays_a_dictionary()
    {
        // Non-scalar shapes (mappings, sequences) must fall through to
        // YamlDotNet's standard handlers — Apt etc. need to retain their
        // structured contents so CloudConfigSerializer's presence-check
        // can distinguish "set to {}" from "omitted entirely". The shape
        // here mirrors cloud-init's documented apt.sources schema (each
        // dict value is itself a mapping with source/keyid/etc fields).
        const string yaml = """
                            apt:
                              sources:
                                my-source:
                                  source: deb http://example /
                                  keyid: ABCD1234
                            """;

        var config = CloudConfigYamlSerializer.Deserialize(yaml);

        config.Apt.Should().NotBeNull();
        config.Apt!.Sources.Should().ContainKey("my-source");
        config.Apt.Sources!["my-source"].Source.Should().Be("deb http://example /");
    }

    [Fact]
    public void Typo_in_known_key_invokes_the_callback()
    {
        // The whole reason cloud-init logs at Warning instead of silent
        // ignore: "hsotname" is a typo, and the operator wants to find out
        // BEFORE production why their hostname didn't change.
        var seen = new List<string>();
        const string yaml = "hsotname: oops";

        var config = CloudConfigYamlSerializer.Deserialize(yaml, onUnknownKey: seen.Add);

        config.Hostname.Should().BeNull();
        seen.Should().BeEquivalentTo("hsotname");
    }
}
