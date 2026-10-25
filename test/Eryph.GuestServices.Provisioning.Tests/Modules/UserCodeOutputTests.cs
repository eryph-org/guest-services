using AwesomeAssertions;
using Eryph.GuestServices.Provisioning.Modules;

namespace Eryph.GuestServices.Provisioning.Tests.Modules;

public sealed class UserCodeOutputTests
{
    [Fact]
    public void Tail_includes_both_streams_labelled_when_both_present()
    {
        // A failing script may report via either stream, so both are surfaced.
        var tail = UserCodeOutput.Tail("normal output", "the error");

        tail.Should().Contain("stderr: the error");
        tail.Should().Contain("stdout: normal output");
    }

    [Fact]
    public void Tail_emits_only_the_present_stream()
    {
        UserCodeOutput.Tail("only stdout", "   ").Should().Be("stdout: only stdout");
        UserCodeOutput.Tail("   ", "only stderr").Should().Be("stderr: only stderr");
    }

    [Fact]
    public void Tail_returns_empty_when_both_streams_are_empty()
    {
        UserCodeOutput.Tail("", null).Should().BeEmpty();
    }

    [Fact]
    public void Tail_keeps_the_trailing_slice_and_marks_truncation()
    {
        // The actual error is the last thing written, so truncation keeps the
        // tail, not the head.
        var text = new string('a', 100) + "THE REAL ERROR";

        var tail = UserCodeOutput.Tail(stdout: null, stderr: text, maxChars: 20);

        tail.Should().StartWith("stderr: …(truncated)");
        tail.Should().EndWith("THE REAL ERROR");
        tail.Should().NotContain("aaaaaaaaaa"); // the head was dropped
    }

    [Fact]
    public void Tail_splits_the_budget_so_one_stream_cannot_crowd_out_the_other()
    {
        // Both present: each gets about half the budget, so a huge stdout cannot
        // bury the stderr content entirely.
        var tail = UserCodeOutput.Tail(new string('o', 400), new string('e', 400), maxChars: 40);

        tail.Should().Contain("stderr:");
        tail.Should().Contain("stdout:");
        tail.Should().Contain("eeeee");
        tail.Should().Contain("ooooo");
    }

    [Fact]
    public void Tail_clamps_a_nonpositive_budget_instead_of_throwing()
    {
        var act = () => UserCodeOutput.Tail(stdout: null, stderr: "abcdef", maxChars: 0);

        act.Should().NotThrow();
        act().Should().Contain("stderr:");
    }

    [Fact]
    public void AppendTail_appends_output_to_the_reason_or_leaves_it_alone()
    {
        UserCodeOutput.AppendTail("boom", stdout: null, stderr: "detail")
            .Should().Be("boom\nstderr: detail");
        UserCodeOutput.AppendTail("boom", stdout: "", stderr: "   ")
            .Should().Be("boom");
    }
}
