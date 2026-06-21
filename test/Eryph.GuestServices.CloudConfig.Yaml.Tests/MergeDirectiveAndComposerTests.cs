using AwesomeAssertions;
using Eryph.GuestServices.CloudConfig;
using Eryph.GuestServices.CloudConfig.Yaml;

namespace Eryph.GuestServices.CloudConfig.Yaml.Tests;

/// <summary>
/// merge_how / merge_type directive extraction (RFC 0032) and the composer's
/// honouring of it when pre-merging fragments.
/// </summary>
public sealed class MergeDirectiveAndComposerTests
{
    [Fact]
    public void Absent_directive_reads_as_null()
    {
        MergeDirectiveReader.Read("hostname: web01").Should().BeNull();
    }

    [Fact]
    public void Reads_string_form_merge_how()
    {
        var options = MergeDirectiveReader.Read("merge_how: 'list(replace)+dict(replace)'\nhostname: web01");

        options.Should().NotBeNull();
        options!.List.Should().Be(ListMergeAction.Replace);
        options.Dict.Should().Be(DictMergeAction.Replace);
    }

    [Fact]
    public void Reads_merge_type_alias()
    {
        var options = MergeDirectiveReader.Read("merge_type: list(prepend)");

        options!.List.Should().Be(ListMergeAction.Prepend);
    }

    [Fact]
    public void Reads_structured_list_form()
    {
        const string yaml = """
                            merge_how:
                              - name: list
                                settings: [replace]
                              - name: dict
                                settings: [recurse_list]
                            ssh_authorized_keys: [k]
                            """;

        var options = MergeDirectiveReader.Read(yaml);

        options!.List.Should().Be(ListMergeAction.Replace);
        options.Dict.Should().Be(DictMergeAction.Recurse);
    }

    [Fact]
    public void Composer_default_appends_lists()
    {
        var model = CloudConfigComposer.MergeToModel(
        [
            "ssh_authorized_keys: [a]",
            "ssh_authorized_keys: [b]",
        ]);

        model!.SshAuthorizedKeys.Should().Equal("a", "b");
    }

    [Fact]
    public void Composer_honours_list_replace_directive_on_incoming_fragment()
    {
        var model = CloudConfigComposer.MergeToModel(
        [
            "ssh_authorized_keys: [a, b]",
            "merge_how: list(replace)\nssh_authorized_keys: [c]",
        ]);

        model!.SshAuthorizedKeys.Should().Equal("c");
    }

    [Fact]
    public void Composer_directive_on_first_fragment_does_not_affect_outcome()
    {
        // The first fragment has nothing to merge onto, so its directive is moot.
        var model = CloudConfigComposer.MergeToModel(
        [
            "merge_how: list(replace)\nssh_authorized_keys: [a]",
            "ssh_authorized_keys: [b]",
        ]);

        model!.SshAuthorizedKeys.Should().Equal("a", "b");
    }

    [Fact]
    public void Composer_replace_then_append_keeps_replaced_plus_later()
    {
        // a, then b(replace) -> [b], then c(append) -> [b, c]. Each directive
        // acts on the accumulator produced by the fragments before it.
        var model = CloudConfigComposer.MergeToModel(
        [
            "ssh_authorized_keys: [a]",
            "merge_how: list(replace)\nssh_authorized_keys: [b]",
            "ssh_authorized_keys: [c]",
        ]);

        model!.SshAuthorizedKeys.Should().Equal("b", "c");
    }

    [Fact]
    public void Composer_append_then_replace_keeps_only_the_last()
    {
        // a, then b(append) -> [a, b], then c(replace) -> [c]. A late replace
        // drops everything accumulated so far.
        var model = CloudConfigComposer.MergeToModel(
        [
            "ssh_authorized_keys: [a]",
            "ssh_authorized_keys: [b]",
            "merge_how: list(replace)\nssh_authorized_keys: [c]",
        ]);

        model!.SshAuthorizedKeys.Should().Equal("c");
    }

    [Fact]
    public void Composer_no_replace_in_the_middle_protects_the_accumulator()
    {
        // a, then b(no_replace) -> keeps [a], then c(append) -> [a, c]. The
        // middle fragment's keys are dropped because the accumulator is non-empty.
        var model = CloudConfigComposer.MergeToModel(
        [
            "ssh_authorized_keys: [a]",
            "merge_how: list(no_replace)\nssh_authorized_keys: [b]",
            "ssh_authorized_keys: [c]",
        ]);

        model!.SshAuthorizedKeys.Should().Equal("a", "c");
    }

    [Fact]
    public void Merge_how_key_does_not_warn_as_unknown()
    {
        var unknown = new List<string>();

        CloudConfigYamlSerializer.Deserialize("merge_how: list(replace)\nhostname: web01", unknown.Add);

        unknown.Should().NotContain("merge_how");
    }
}
