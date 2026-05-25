using AwesomeAssertions;
using Eryph.GuestServices.CloudConfig;
using Eryph.GuestServices.CloudConfig.Yaml;

namespace Eryph.GuestServices.CloudConfig.Yaml.Tests;

/// <summary>
/// End-to-end YAML round-trip coverage for the PyYAML SafeLoader integer
/// grammar across representative <c>int?</c> properties on both the
/// cloud-config and network-config models. Every leading-zero-octal /
/// underscore row here would have parsed to a SILENT wrong value (octal
/// read as decimal) or thrown before the YAML 1.1 integer fix.
/// </summary>
public sealed class Int11RoundTripTests
{
    [Theory]
    [InlineData("1500", 1500)]
    [InlineData("0644", 420)]      // leading-zero octal — was silently read as 644
    [InlineData("1_500", 1500)]    // underscore separator — was a parse error
    [InlineData("0x5DC", 1500)]    // hex
    public void Network_ethernet_mtu_parses_YAML11_integer_forms(string token, int expected)
    {
        var yaml = $"version: 2\nethernets:\n  eth0:\n    mtu: {token}";

        var config = NetworkConfigYamlSerializer.Deserialize(yaml);

        config.Ethernets!["eth0"].Mtu.Should().Be(expected);
    }

    [Theory]
    [InlineData("100", 100)]
    [InlineData("0100", 64)]       // octal 100 = 64
    [InlineData("1_0", 10)]
    public void Network_route_metric_parses_YAML11_integer_forms(string token, int expected)
    {
        var yaml =
            "version: 2\n" +
            "ethernets:\n" +
            "  eth0:\n" +
            "    routes:\n" +
            $"    - to: 0.0.0.0/0\n      via: 10.0.0.1\n      metric: {token}";

        var config = NetworkConfigYamlSerializer.Deserialize(yaml);

        config.Ethernets!["eth0"].Routes![0].Metric.Should().Be(expected);
    }

    [Theory]
    [InlineData("1000", 1000)]
    [InlineData("0644", 420)]
    [InlineData("1_000", 1000)]
    public void User_uid_parses_YAML11_integer_forms(string token, int expected)
    {
        var yaml = $"users:\n- name: alice\n  uid: {token}";

        var config = CloudConfigYamlSerializer.Deserialize(yaml);

        config.Users![0].Uid.Should().Be(expected);
    }

    [Theory]
    [InlineData("5", 5)]
    [InlineData("010", 8)]         // octal
    [InlineData("1_0", 10)]
    public void PhoneHome_tries_parses_YAML11_integer_forms(string token, int expected)
    {
        var yaml = $"phone_home:\n  url: http://example.invalid/\n  tries: {token}";

        var config = CloudConfigYamlSerializer.Deserialize(yaml);

        config.PhoneHome!.Tries.Should().Be(expected);
    }

    [Theory]
    [InlineData("10", 10)]
    [InlineData("0644", 420)]
    [InlineData("1_5", 15)]
    public void YumRepo_priority_parses_YAML11_integer_forms(string token, int expected)
    {
        var yaml = $"yum_repos:\n  myrepo:\n    baseurl: http://example.invalid/\n    priority: {token}";

        var config = CloudConfigYamlSerializer.Deserialize(yaml);

        config.YumRepos!["myrepo"].Priority.Should().Be(expected);
    }

    [Fact]
    public void Quoted_integer_on_int_target_still_parses()
    {
        // The int? target declares the intent — a quoted `"1500"` coerces the
        // same way cloud-init's modules do (matching the bool-case tolerance).
        var yaml = "version: 2\nethernets:\n  eth0:\n    mtu: \"0644\"";

        var config = NetworkConfigYamlSerializer.Deserialize(yaml);

        config.Ethernets!["eth0"].Mtu.Should().Be(420);
    }

    [Fact]
    public void Empty_scalar_on_nullable_int_yields_null()
    {
        var yaml = "version: 2\nethernets:\n  eth0:\n    mtu:";

        var config = NetworkConfigYamlSerializer.Deserialize(yaml);

        config.Ethernets!["eth0"].Mtu.Should().BeNull();
    }

    [Fact]
    public void Bogus_text_on_int_target_throws_locatable_error()
    {
        var yaml = "users:\n- name: alice\n  uid: notanumber";

        var act = () => CloudConfigYamlSerializer.Deserialize(yaml);

        act.Should().Throw<Eryph.ConfigModel.InvalidConfigException>();
    }
}
