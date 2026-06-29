using AwesomeAssertions;
using Eryph.GuestServices.CloudConfig;

namespace Eryph.GuestServices.CloudConfig.Tests;

/// <summary>
/// The merge engine honours a per-fragment cloud-init merge_how directive
/// (RFC 0032). The default (parameterless) overload is covered elsewhere; these
/// pin the non-default list/dict actions.
/// </summary>
public sealed class CloudConfigMergeOptionsTests
{
    private static CloudConfig WithKeys(params string[] keys) =>
        new() { SshAuthorizedKeys = keys };

    [Fact]
    public void Default_appends_lists()
    {
        var merged = CloudConfigMerge.Merge(WithKeys("a"), WithKeys("b"));

        merged.SshAuthorizedKeys.Should().Equal("a", "b");
    }

    [Fact]
    public void List_replace_drops_the_accumulated_list()
    {
        var options = CloudInitMergeOptions.Parse("list(replace)");

        var merged = CloudConfigMerge.Merge(WithKeys("a", "b"), WithKeys("c"), options);

        merged.SshAuthorizedKeys.Should().Equal("c");
    }

    [Fact]
    public void List_prepend_puts_incoming_first()
    {
        var options = CloudInitMergeOptions.Parse("list(prepend)");

        var merged = CloudConfigMerge.Merge(WithKeys("a"), WithKeys("b"), options);

        merged.SshAuthorizedKeys.Should().Equal("b", "a");
    }

    [Fact]
    public void List_no_replace_keeps_the_accumulated_list()
    {
        var options = CloudInitMergeOptions.Parse("list(no_replace)");

        var merged = CloudConfigMerge.Merge(WithKeys("a"), WithKeys("b"), options);

        merged.SshAuthorizedKeys.Should().Equal("a");
    }

    [Fact]
    public void List_replace_with_absent_incoming_keeps_left()
    {
        // A fragment that does not set the key (right is null) leaves the
        // accumulated list even under list(replace).
        var options = CloudInitMergeOptions.Parse("list(replace)");

        var merged = CloudConfigMerge.Merge(WithKeys("a", "b"), new CloudConfig(), options);

        merged.SshAuthorizedKeys.Should().Equal("a", "b");
    }

    [Fact]
    public void Users_replace_drops_the_whole_keyed_list()
    {
        var left = new CloudConfig
        {
            Users = [new UserConfig { Name = "admin" }, new UserConfig { Name = "deploy" }],
        };
        var right = new CloudConfig { Users = [new UserConfig { Name = "root" }] };
        var options = CloudInitMergeOptions.Parse("list(replace)");

        var merged = CloudConfigMerge.Merge(left, right, options);

        merged.Users!.Select(u => u.Name).Should().Equal("root");
    }

    [Fact]
    public void Users_no_replace_keeps_the_accumulated_list()
    {
        var left = new CloudConfig { Users = [new UserConfig { Name = "admin" }] };
        var right = new CloudConfig { Users = [new UserConfig { Name = "root" }] };
        var options = CloudInitMergeOptions.Parse("list(no_replace)");

        var merged = CloudConfigMerge.Merge(left, right, options);

        merged.Users!.Select(u => u.Name).Should().Equal("admin");
    }

    [Fact]
    public void Users_prepend_puts_incoming_first_and_still_merges_by_name()
    {
        var left = new CloudConfig
        {
            Users = [new UserConfig { Name = "admin", Groups = ["sudo"] }],
        };
        var right = new CloudConfig
        {
            Users =
            [
                new UserConfig { Name = "deploy" },
                new UserConfig { Name = "admin", Groups = ["docker"] },
            ],
        };
        var options = CloudInitMergeOptions.Parse("list(prepend)");

        var merged = CloudConfigMerge.Merge(left, right, options);

        // Incoming entries lead; the shared "admin" is deep-merged. The prepend
        // directive applies recursively, so the nested groups also prepend the
        // incoming (docker) before the accumulated (sudo).
        merged.Users!.Select(u => u.Name).Should().Equal("deploy", "admin");
        var admin = merged.Users!.Single(u => u.Name == "admin");
        admin.Groups.Should().Equal("docker", "sudo");
    }

    [Fact]
    public void Users_default_merges_by_name()
    {
        var left = new CloudConfig { Users = [new UserConfig { Name = "admin", Groups = ["sudo"] }] };
        var right = new CloudConfig { Users = [new UserConfig { Name = "admin", Groups = ["docker"] }] };

        var merged = CloudConfigMerge.Merge(left, right);

        merged.Users.Should().ContainSingle();
        merged.Users![0].Groups.Should().Equal("sudo", "docker");
    }
}
