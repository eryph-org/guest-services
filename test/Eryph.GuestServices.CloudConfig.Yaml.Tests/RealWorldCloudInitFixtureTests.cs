using AwesomeAssertions;
using Eryph.ConfigModel;
using Eryph.GuestServices.CloudConfig;
using Eryph.GuestServices.CloudConfig.Yaml;

namespace Eryph.GuestServices.CloudConfig.Yaml.Tests;

/// <summary>
/// Drives the deserializer against cloud-config fixtures captured from
/// cloud-init upstream (Apache-2.0; see test/fixtures/cloud-init/README.md)
/// plus one hand-authored lowercase-bool-token fixture. Asserts they parse
/// cleanly and that the YAML 1.1 bool tokens resolve to the same runtime
/// types cloud-init's PyYAML SafeLoader would produce.
/// </summary>
public sealed class RealWorldCloudInitFixtureTests
{
    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", "cloud-init", name);

    private static string Load(string name)
    {
        var path = FixturePath(name);
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"Fixture missing at {path}. Check the test csproj CopyToOutputDirectory wiring.",
                path);
        return File.ReadAllText(path);
    }

    [Theory]
    [InlineData("cloud-config-master-example.yaml")]
    [InlineData("cloud-config-apt.yaml")]
    [InlineData("cloud-config-update-packages.yaml")]
    [InlineData("eryph-yaml11-lowercase-bool-tokens.yaml")]
    [InlineData("eryph-yaml11-integer-forms.yaml")]
    public void Fixture_parses_without_error(string name)
    {
        var yaml = Load(name);

        var act = () => CloudConfigYamlSerializer.Deserialize(yaml);

        act.Should().NotThrow<InvalidConfigException>(
            $"the real-world cloud-init fixture '{name}' must deserialize cleanly");
    }

    [Fact]
    public void MasterExample_capitalised_bool_tokens_resolve_to_bool()
    {
        // doc/examples/cloud-config.txt — the canonical exhaustive example.
        var config = CloudConfigYamlSerializer.Deserialize(Load("cloud-config-master-example.yaml"));

        config.PackageUpdate.Should().BeFalse();      // package_update: false
        config.PackageUpgrade.Should().BeTrue();      // package_upgrade: true
        config.DisableEc2Metadata.Should().BeTrue();  // disable_ec2_metadata: true
        config.DisableRoot.Should().BeFalse();        // disable_root: false
        config.SshPwauth.Should().BeTrue();           // ssh_pwauth: True
        config.Chpasswd!.Expire.Should().BeFalse();   // chpasswd: { expire: False }

        // resize_rootfs: True — bool|string union, plain bool token → bool.
        config.ResizeRootfs.IsBool.Should().BeTrue();
        config.ResizeRootfs.Bool.Should().BeTrue();
    }

    [Fact]
    public void AptExample_pipelining_false_and_mixed_case_bools_round_trip()
    {
        var config = CloudConfigYamlSerializer.Deserialize(Load("cloud-config-apt.yaml"));

        // apt_pipelining stays object? (3-way bool|"none"|int union). The
        // plain bool token resolves natively via the YAML 1.1 resolver.
        config.AptPipelining.Should().Be(false);

        // apt.preserve_sources_list: true (lowercase) — typed bool? member.
        config.Apt.Should().NotBeNull();
        config.Apt!.PreserveSourcesList.Should().BeTrue();
    }

    [Fact]
    public void UpdatePackagesExample_resolves_bool()
    {
        var config = CloudConfigYamlSerializer.Deserialize(Load("cloud-config-update-packages.yaml"));

        config.PackageUpgrade.Should().BeTrue();
    }

    [Fact]
    public void LowercaseTokens_fixture_resolves_YAML11_bools_like_PyYAML()
    {
        // Our own contribution — the lowercase YAML 1.1 tokens cloud-init's
        // example corpus never uses. Every assertion here would have thrown
        // YamlException before the PyYAML SafeLoader compatibility fix.
        var config = CloudConfigYamlSerializer.Deserialize(Load("eryph-yaml11-lowercase-bool-tokens.yaml"));

        config.PackageUpdate.Should().BeTrue();              // yes
        config.PackageUpgrade.Should().BeFalse();            // no
        config.PackageRebootIfRequired.Should().BeTrue();    // on
        config.PreserveHostname.Should().BeFalse();          // off
        config.SshPwauth.Should().BeTrue();                  // y
        config.DisableRoot.Should().BeFalse();               // n
        config.Ntp!.Enabled.Should().BeTrue();               // yes (nested)
        config.Chpasswd!.Expire.Should().BeFalse();          // no (nested)
        config.Users![0].LockPasswd.Should().BeFalse();      // no (list entry)

        // manage_etc_hosts: yes / resize_rootfs: no — BoolOrString unions.
        config.ManageEtcHosts.IsBool.Should().BeTrue();
        config.ManageEtcHosts.Bool.Should().BeTrue();
        config.ResizeRootfs.IsBool.Should().BeTrue();
        config.ResizeRootfs.Bool.Should().BeFalse();

        // power_state.condition: yes — BoolOrString union, plain bool token.
        config.PowerState!.Condition.IsBool.Should().BeTrue();
        config.PowerState.Condition.Bool.Should().BeTrue();
    }

    [Fact]
    public void IntegerForms_fixture_resolves_YAML11_integers_like_PyYAML()
    {
        // Our own contribution — the YAML 1.1 integer forms cloud-init's
        // example corpus never uses. The octal/underscore rows would have
        // silently mis-parsed (octal as decimal) or thrown before the
        // YAML 1.1 integer fix.
        var config = CloudConfigYamlSerializer.Deserialize(Load("eryph-yaml11-integer-forms.yaml"));

        config.AptPipelining.Should().Be(420L);                  // 0644 octal, object?
        config.PhoneHome!.Tries.Should().Be(5);                  // 0b101 binary
        config.Users!.Single(u => u.Name == "octal-uid").Uid.Should().Be(420);       // 0644
        config.Users!.Single(u => u.Name == "underscore-uid").Uid.Should().Be(1000); // 1_000
        config.Users!.Single(u => u.Name == "hex-uid").Uid.Should().Be(1000);        // 0x3E8
        config.YumRepos!["base"].Priority.Should().Be(8);        // 010 octal
    }
}
