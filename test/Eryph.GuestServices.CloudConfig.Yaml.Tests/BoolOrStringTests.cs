using AwesomeAssertions;
using Eryph.GuestServices.CloudConfig;
using Eryph.GuestServices.CloudConfig.Yaml;

namespace Eryph.GuestServices.CloudConfig.Yaml.Tests;

/// <summary>
/// PyYAML quoting-distinction coverage for the three bool|string union
/// fields that cloud-init documents and we model with
/// <see cref="BoolOrString"/>: <c>manage_etc_hosts</c>,
/// <c>resize_rootfs</c>, <c>power_state.condition</c>.
/// </summary>
/// <remarks>
/// The contract:
/// <list type="bullet">
///   <item>A plain YAML 1.1 bool token (true/false/yes/no/on/off/y/n + variants)
///   resolves to the bool variant.</item>
///   <item>A quoted scalar with the same text stays a string — the
///   operator quoted intentionally.</item>
///   <item>A non-bool plain scalar (e.g. "localhost", "noblock", a shell
///   command) is a string.</item>
///   <item>An omitted / empty scalar is <see cref="BoolOrString.Empty"/>.</item>
/// </list>
/// </remarks>
public sealed class BoolOrStringTests
{
    // ---------------------------------------------------------------------
    // manage_etc_hosts
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData("true", true)]
    [InlineData("yes", true)]
    [InlineData("on", true)]
    [InlineData("y", true)]
    [InlineData("false", false)]
    [InlineData("no", false)]
    [InlineData("off", false)]
    [InlineData("n", false)]
    public void ManageEtcHosts_plain_YAML11_bool_token_resolves_to_bool(string token, bool expected)
    {
        var config = CloudConfigYamlSerializer.Deserialize($"manage_etc_hosts: {token}");

        config.ManageEtcHosts.IsBool.Should().BeTrue();
        config.ManageEtcHosts.Bool.Should().Be(expected);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("yes")]
    [InlineData("off")]
    public void ManageEtcHosts_quoted_bool_token_stays_a_string(string token)
    {
        var config = CloudConfigYamlSerializer.Deserialize($"manage_etc_hosts: \"{token}\"");

        config.ManageEtcHosts.IsString.Should().BeTrue();
        config.ManageEtcHosts.String.Should().Be(token);
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("template")]
    public void ManageEtcHosts_string_enum_resolves_to_string(string value)
    {
        var config = CloudConfigYamlSerializer.Deserialize($"manage_etc_hosts: {value}");

        config.ManageEtcHosts.IsString.Should().BeTrue();
        config.ManageEtcHosts.String.Should().Be(value);
    }

    [Fact]
    public void ManageEtcHosts_empty_yields_BoolOrString_Empty()
    {
        var config = CloudConfigYamlSerializer.Deserialize("manage_etc_hosts:");

        config.ManageEtcHosts.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void ManageEtcHosts_omitted_yields_BoolOrString_Empty()
    {
        var config = CloudConfigYamlSerializer.Deserialize("hostname: web01");

        config.ManageEtcHosts.IsEmpty.Should().BeTrue();
    }

    // ---------------------------------------------------------------------
    // resize_rootfs
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData("true", true)]
    [InlineData("yes", true)]
    [InlineData("false", false)]
    [InlineData("no", false)]
    public void ResizeRootfs_plain_bool_resolves_to_bool(string token, bool expected)
    {
        var config = CloudConfigYamlSerializer.Deserialize($"resize_rootfs: {token}");

        config.ResizeRootfs.IsBool.Should().BeTrue();
        config.ResizeRootfs.Bool.Should().Be(expected);
    }

    [Fact]
    public void ResizeRootfs_quoted_bool_token_stays_a_string()
    {
        var config = CloudConfigYamlSerializer.Deserialize("resize_rootfs: \"yes\"");

        config.ResizeRootfs.IsString.Should().BeTrue();
        config.ResizeRootfs.String.Should().Be("yes");
    }

    [Fact]
    public void ResizeRootfs_documented_noblock_string_resolves_to_string()
    {
        var config = CloudConfigYamlSerializer.Deserialize("resize_rootfs: noblock");

        config.ResizeRootfs.IsString.Should().BeTrue();
        config.ResizeRootfs.String.Should().Be("noblock");
    }

    // ---------------------------------------------------------------------
    // power_state.condition
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData("true", true)]
    [InlineData("yes", true)]
    [InlineData("false", false)]
    [InlineData("no", false)]
    public void PowerStateCondition_plain_bool_resolves_to_bool(string token, bool expected)
    {
        var yaml = $"power_state:\n  condition: {token}";

        var config = CloudConfigYamlSerializer.Deserialize(yaml);

        config.PowerState!.Condition.IsBool.Should().BeTrue();
        config.PowerState.Condition.Bool.Should().Be(expected);
    }

    [Fact]
    public void PowerStateCondition_quoted_true_is_the_shell_command_true()
    {
        // Cloud-init semantic: `condition: "true"` is the Linux shell
        // command /bin/true, which exits 0. Must land as string so the
        // module dispatches via RunShellCommandAsync, not as a bool.
        var yaml = "power_state:\n  condition: \"true\"";

        var config = CloudConfigYamlSerializer.Deserialize(yaml);

        config.PowerState!.Condition.IsString.Should().BeTrue();
        config.PowerState.Condition.String.Should().Be("true");
    }

    [Fact]
    public void PowerStateCondition_shell_command_string_resolves_to_string()
    {
        var yaml = "power_state:\n  condition: 'exit 0'";

        var config = CloudConfigYamlSerializer.Deserialize(yaml);

        config.PowerState!.Condition.IsString.Should().BeTrue();
        config.PowerState.Condition.String.Should().Be("exit 0");
    }

    [Fact]
    public void PowerStateCondition_omitted_yields_Empty()
    {
        var yaml = "power_state:\n  mode: reboot";

        var config = CloudConfigYamlSerializer.Deserialize(yaml);

        config.PowerState!.Condition.IsEmpty.Should().BeTrue();
    }

    // ---------------------------------------------------------------------
    // Direct primitive coverage (sanity)
    // ---------------------------------------------------------------------

    [Fact]
    public void BoolOrString_default_is_Empty()
    {
        default(BoolOrString).IsEmpty.Should().BeTrue();
        BoolOrString.Empty.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void BoolOrString_FromBool_carries_the_value()
    {
        BoolOrString.FromBool(true).IsBool.Should().BeTrue();
        BoolOrString.FromBool(true).Bool.Should().Be(true);
        BoolOrString.FromBool(false).Bool.Should().Be(false);
    }

    [Fact]
    public void BoolOrString_FromString_carries_the_value()
    {
        BoolOrString.FromString("hello").IsString.Should().BeTrue();
        BoolOrString.FromString("hello").String.Should().Be("hello");
    }
}
