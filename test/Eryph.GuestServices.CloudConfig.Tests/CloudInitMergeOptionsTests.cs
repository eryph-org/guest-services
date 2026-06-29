using AwesomeAssertions;
using Eryph.GuestServices.CloudConfig;

namespace Eryph.GuestServices.CloudConfig.Tests;

public sealed class CloudInitMergeOptionsTests
{
    [Fact]
    public void Blank_directive_is_the_cloud_init_default()
    {
        CloudInitMergeOptions.Parse(null).Should().BeSameAs(CloudInitMergeOptions.CloudInitDefault);
        CloudInitMergeOptions.Parse("   ").Should().BeSameAs(CloudInitMergeOptions.CloudInitDefault);

        var def = CloudInitMergeOptions.CloudInitDefault;
        def.List.Should().Be(ListMergeAction.Append);
        def.Dict.Should().Be(DictMergeAction.Recurse);
        def.Str.Should().Be(StrMergeAction.Replace);
    }

    [Fact]
    public void Parses_list_actions()
    {
        CloudInitMergeOptions.Parse("list(replace)").List.Should().Be(ListMergeAction.Replace);
        CloudInitMergeOptions.Parse("list(prepend)").List.Should().Be(ListMergeAction.Prepend);
        CloudInitMergeOptions.Parse("list(append)").List.Should().Be(ListMergeAction.Append);
    }

    [Fact]
    public void No_replace_is_not_mistaken_for_replace()
    {
        // "no_replace" contains "replace" as a substring — token matching must
        // not collapse it into Replace.
        CloudInitMergeOptions.Parse("list(no_replace)").List.Should().Be(ListMergeAction.NoReplace);
    }

    [Fact]
    public void Parses_full_cloud_init_default_string_form()
    {
        var options = CloudInitMergeOptions.Parse("list(append)+dict(no_replace,recurse_list)+str()");

        options.List.Should().Be(ListMergeAction.Append);
        options.Dict.Should().Be(DictMergeAction.Recurse);
        options.Str.Should().Be(StrMergeAction.Replace);
    }

    [Fact]
    public void Parses_dict_and_str_overrides()
    {
        var options = CloudInitMergeOptions.Parse("dict(replace)+str(append)");

        options.Dict.Should().Be(DictMergeAction.Replace);
        options.Str.Should().Be(StrMergeAction.Append);
    }

    [Fact]
    public void Unknown_mergers_and_settings_are_ignored()
    {
        var options = CloudInitMergeOptions.Parse("list(replace,recurse_array)+frob(whatever)");

        options.List.Should().Be(ListMergeAction.Replace);
        options.Dict.Should().Be(DictMergeAction.Recurse);
    }

    [Fact]
    public void Bare_merger_name_without_parens_is_tolerated()
    {
        CloudInitMergeOptions.Parse("dict+list(replace)").List.Should().Be(ListMergeAction.Replace);
    }
}
