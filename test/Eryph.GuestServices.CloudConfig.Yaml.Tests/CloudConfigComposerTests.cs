using AwesomeAssertions;
using Eryph.GuestServices.CloudConfig;
using Eryph.GuestServices.CloudConfig.Yaml;

namespace Eryph.GuestServices.CloudConfig.Yaml.Tests;

/// <summary>
/// Coverage for <see cref="CloudConfigComposer"/> — the host-side facade eryph
/// uses to pre-merge cloud-config fodder into one document. Precedence is
/// fragment order (later wins on scalars, lists concatenate), per RFC 0032.
/// </summary>
public sealed class CloudConfigComposerTests
{
    [Fact]
    public void Returns_null_when_no_fragments()
    {
        CloudConfigComposer.MergeToModel([]).Should().BeNull();
        CloudConfigComposer.Merge([]).Should().BeNull();
    }

    [Fact]
    public void Skips_empty_and_whitespace_fragments()
    {
        var model = CloudConfigComposer.MergeToModel(["", "   ", "hostname: web01"]);

        model.Should().NotBeNull();
        model!.Hostname.Should().Be("web01");
    }

    [Fact]
    public void Later_fragment_wins_on_scalar_conflict()
    {
        var model = CloudConfigComposer.MergeToModel(
        [
            "hostname: first",
            "hostname: second",
        ]);

        model!.Hostname.Should().Be("second");
    }

    [Fact]
    public void Lists_concatenate_in_fragment_order()
    {
        var model = CloudConfigComposer.MergeToModel(
        [
            "ssh_authorized_keys: [keyA]",
            "ssh_authorized_keys: [keyB, keyC]",
        ]);

        model!.SshAuthorizedKeys.Should().Equal("keyA", "keyB", "keyC");
    }

    [Fact]
    public void Users_merge_by_name_across_fragments()
    {
        var model = CloudConfigComposer.MergeToModel(
        [
            """
            users:
              - name: admin
                groups: [sudo]
            """,
            """
            users:
              - name: admin
                groups: [docker]
              - name: deploy
            """,
        ]);

        model!.Users.Should().HaveCount(2);
        var admin = model.Users!.Single(u => u.Name == "admin");
        admin.Groups.Should().Equal("sudo", "docker");
        model.Users.Should().Contain(u => u.Name == "deploy");
    }

    [Fact]
    public void Merge_produces_a_single_serialisable_cloud_config_document()
    {
        var doc = CloudConfigComposer.Merge(
        [
            "hostname: web01\nssh_authorized_keys: [keyA]",
            "ssh_authorized_keys: [keyB]\nruncmd:\n  - echo hi",
        ]);

        doc.Should().StartWith("#cloud-config\n");

        // The composed document must itself parse back to the merged model.
        var reparsed = CloudConfigYamlSerializer.Deserialize(doc!);
        reparsed.Hostname.Should().Be("web01");
        reparsed.SshAuthorizedKeys.Should().Equal("keyA", "keyB");
        reparsed.Runcmd.Should().ContainSingle();
        reparsed.Runcmd![0].Command.Should().Be("echo hi");
    }

    [Fact]
    public void Unknown_keys_surface_through_the_callback()
    {
        var unknown = new List<string>();

        CloudConfigComposer.MergeToModel(
            ["hostname: web01\nnot_a_real_key: 1"],
            unknown.Add);

        unknown.Should().Contain("not_a_real_key");
    }
}
