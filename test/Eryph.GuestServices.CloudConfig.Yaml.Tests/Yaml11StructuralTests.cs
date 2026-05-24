using AwesomeAssertions;
using Eryph.ConfigModel;
using Eryph.GuestServices.CloudConfig;
using Eryph.GuestServices.CloudConfig.Yaml;

namespace Eryph.GuestServices.CloudConfig.Yaml.Tests;

/// <summary>
/// Covers the structural YAML 1.1 / PyYAML SafeLoader parity points beyond
/// scalar typing: object? integer resolution, merge keys, duplicate-key
/// handling, and the deliberate string-coercion divergence.
/// </summary>
public sealed class Yaml11StructuralTests
{
    [Fact]
    public void Object_target_resolves_leading_zero_octal_to_long()
    {
        // apt_pipelining is object? (cloud-init's bool | "none" | int union).
        // A plain `0644` resolves through the YAML 1.1 integer grammar to the
        // long 420 — the same value PyYAML's safe_load would hand the module.
        var config = CloudConfigYamlSerializer.Deserialize("apt_pipelining: 0644");

        config.AptPipelining.Should().Be(420L);
    }

    [Fact]
    public void Object_target_resolves_underscore_integer_to_long()
    {
        var config = CloudConfigYamlSerializer.Deserialize("apt_pipelining: 1_000");

        config.AptPipelining.Should().Be(1000L);
    }

    [Fact]
    public void Object_target_keeps_colon_scalar_as_string()
    {
        // Sexagesimal carve-out: `12:30` stays a string, NOT base-60 750.
        var config = CloudConfigYamlSerializer.Deserialize("apt_pipelining: 12:30");

        config.AptPipelining.Should().Be("12:30");
    }

    [Fact]
    public void Merge_key_merges_anchor_into_mapping()
    {
        // YAML 1.1 merge key (`<<: *anchor`). PyYAML SafeLoader expands it;
        // we wrap the parser in MergingParser to match. The base user block
        // is factored into an anchor and merged into a concrete user; the
        // merged-in keys must be present on the result.
        const string yaml =
            "users:\n" +
            "- &base\n" +
            "  sudo: ALL=(ALL) NOPASSWD:ALL\n" +
            "  lock_passwd: false\n" +
            "- <<: *base\n" +
            "  name: alice\n";

        var config = CloudConfigYamlSerializer.Deserialize(yaml);

        // The second entry is the merged one (the anchor entry has no name).
        var alice = config.Users!.Single(u => u.Name == "alice");
        alice.LockPasswd.Should().BeFalse();             // merged in from *base
        alice.Sudo.Should().Contain("ALL=(ALL) NOPASSWD:ALL"); // merged in from *base
    }

    [Fact]
    public void Merge_key_local_key_overrides_merged_value()
    {
        // When both the anchor and the local mapping define a key, the local
        // value wins — standard YAML 1.1 merge-key semantics.
        const string yaml =
            "users:\n" +
            "- &base\n" +
            "  lock_passwd: true\n" +
            "- <<: *base\n" +
            "  name: bob\n" +
            "  lock_passwd: false\n";

        var config = CloudConfigYamlSerializer.Deserialize(yaml);

        config.Users!.Single(u => u.Name == "bob").LockPasswd.Should().BeFalse();
    }

    [Fact]
    public void Duplicate_top_level_key_is_last_wins()
    {
        // Probe / decision pin. In our configured deserializer YamlDotNet
        // 16.3 does NOT throw on a duplicate mapping key — it applies the
        // value last, matching PyYAML safe_load's silent last-wins. We pin
        // that observed behaviour here; the divergence note in
        // differences-from-cloud-init.md records that we considered the
        // stricter (throw) option but the library defaults to last-wins and
        // that happens to match cloud-init, so no override is warranted.
        const string yaml = "hostname: a\nhostname: b";

        var config = CloudConfigYamlSerializer.Deserialize(yaml);

        config.Hostname.Should().Be("b");
    }

    [Fact]
    public void String_target_preserves_literal_bool_like_scalar()
    {
        // String-coercion divergence (deliberate): cloud-init resolves the
        // implicit type then str()s it, so `hostname: NO` becomes the literal
        // string "False". We preserve the operator's literal text — no Norway
        // problem. Documented in differences-from-cloud-init.md.
        var config = CloudConfigYamlSerializer.Deserialize("hostname: NO");

        config.Hostname.Should().Be("NO");
    }

    [Fact]
    public void String_target_preserves_literal_numeric_scalar()
    {
        // cloud-init's resolve-then-stringify mangles `1.10` to "1.1" and
        // `0123` to "83". We keep the literal text on string-typed fields.
        var config = CloudConfigYamlSerializer.Deserialize("hostname: 1.10");

        config.Hostname.Should().Be("1.10");
    }
}
