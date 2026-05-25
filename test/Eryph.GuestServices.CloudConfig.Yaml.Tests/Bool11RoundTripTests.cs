using AwesomeAssertions;
using Eryph.GuestServices.CloudConfig;
using Eryph.GuestServices.CloudConfig.Yaml;

namespace Eryph.GuestServices.CloudConfig.Yaml.Tests;

/// <summary>
/// End-to-end YAML round-trip coverage for the PyYAML SafeLoader bool
/// expansion. Cross-product of representative <c>bool?</c> properties on
/// the model and the full 22-token set, so every parser path (top-level
/// scalar, nested record, list-of-records) gets exercised.
/// </summary>
/// <remarks>
/// The pre-refactor build threw <c>YamlException</c> on every non-YAML-1.2
/// token. Each row here is a regression pin for one of those throws.
/// </remarks>
public sealed class Bool11RoundTripTests
{
    public static IEnumerable<object[]> AllTrueTokens() =>
    [
        ["true"], ["True"], ["TRUE"],
        ["yes"], ["Yes"], ["YES"],
        ["on"], ["On"], ["ON"],
        ["y"], ["Y"],
    ];

    public static IEnumerable<object[]> AllFalseTokens() =>
    [
        ["false"], ["False"], ["FALSE"],
        ["no"], ["No"], ["NO"],
        ["off"], ["Off"], ["OFF"],
        ["n"], ["N"],
    ];

    public static IEnumerable<object[]> AllBoolTokens() =>
        AllTrueTokens().Select(r => new object[] { r[0], true })
            .Concat(AllFalseTokens().Select(r => new object[] { r[0], false }));

    [Theory]
    [MemberData(nameof(AllBoolTokens))]
    public void TopLevel_package_update_accepts_every_YAML11_bool_token(string token, bool expected)
    {
        var yaml = $"package_update: {token}";

        var config = CloudConfigYamlSerializer.Deserialize(yaml);

        config.PackageUpdate.Should().Be(expected);
    }

    [Theory]
    [MemberData(nameof(AllBoolTokens))]
    public void TopLevel_preserve_hostname_accepts_every_YAML11_bool_token(string token, bool expected)
    {
        var yaml = $"preserve_hostname: {token}";

        var config = CloudConfigYamlSerializer.Deserialize(yaml);

        config.PreserveHostname.Should().Be(expected);
    }

    [Theory]
    [MemberData(nameof(AllBoolTokens))]
    public void TopLevel_ssh_pwauth_accepts_every_YAML11_bool_token(string token, bool expected)
    {
        var yaml = $"ssh_pwauth: {token}";

        var config = CloudConfigYamlSerializer.Deserialize(yaml);

        config.SshPwauth.Should().Be(expected);
    }

    [Theory]
    [MemberData(nameof(AllBoolTokens))]
    public void Nested_chpasswd_expire_accepts_every_YAML11_bool_token(string token, bool expected)
    {
        var yaml = $"chpasswd:\n  expire: {token}";

        var config = CloudConfigYamlSerializer.Deserialize(yaml);

        config.Chpasswd!.Expire.Should().Be(expected);
    }

    [Theory]
    [MemberData(nameof(AllBoolTokens))]
    public void Nested_ntp_enabled_accepts_every_YAML11_bool_token(string token, bool expected)
    {
        var yaml = $"ntp:\n  enabled: {token}";

        var config = CloudConfigYamlSerializer.Deserialize(yaml);

        config.Ntp!.Enabled.Should().Be(expected);
    }

    [Theory]
    [MemberData(nameof(AllBoolTokens))]
    public void ListEntry_user_lock_passwd_accepts_every_YAML11_bool_token(string token, bool expected)
    {
        var yaml = $"users:\n- name: alice\n  lock_passwd: {token}";

        var config = CloudConfigYamlSerializer.Deserialize(yaml);

        config.Users![0].LockPasswd.Should().Be(expected);
    }

    [Theory]
    [MemberData(nameof(AllBoolTokens))]
    public void ListEntry_write_files_defer_accepts_every_YAML11_bool_token(string token, bool expected)
    {
        var yaml = $"write_files:\n- path: /tmp/x\n  content: hi\n  defer: {token}";

        var config = CloudConfigYamlSerializer.Deserialize(yaml);

        config.WriteFiles![0].Defer.Should().Be(expected);
    }

    [Theory]
    [InlineData("True")]
    [InlineData("yes")]
    [InlineData("on")]
    [InlineData("Y")]
    public void Quoted_bool_token_still_parses_to_bool_when_target_is_bool(string token)
    {
        // For typed bool / bool? targets the YAML 1.1 bool tag applies
        // regardless of quoting — cloud-init's runtime resolves the bool
        // before the module sees it. Operators routinely write
        // `package_update: "yes"` and expect it to mean true. Quoting only
        // matters for the bool-vs-string disambiguation on BoolOrString
        // (covered in BoolOrStringTests).
        var yaml = $"package_update: \"{token}\"";

        var config = CloudConfigYamlSerializer.Deserialize(yaml);

        config.PackageUpdate.Should().Be(true);
    }

    [Fact]
    public void Empty_scalar_on_bool_target_yields_null()
    {
        // `key:` with nothing after the colon is a legitimate way to clear
        // an optional bool — must round-trip to null, not throw.
        const string yaml = "package_update:";

        var config = CloudConfigYamlSerializer.Deserialize(yaml);

        config.PackageUpdate.Should().BeNull();
    }

    [Fact]
    public void Bogus_text_on_bool_target_throws_locatable_error()
    {
        // Garbage on a bool-typed field should fail with a YAML location
        // so operators can grep their cloud-config for the line.
        const string yaml = "package_update: maybe";

        var act = () => CloudConfigYamlSerializer.Deserialize(yaml);

        act.Should().Throw<Eryph.ConfigModel.InvalidConfigException>();
    }
}
