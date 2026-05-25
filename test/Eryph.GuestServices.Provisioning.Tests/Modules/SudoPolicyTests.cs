using AwesomeAssertions;
using Eryph.GuestServices.Provisioning.Modules;

namespace Eryph.GuestServices.Provisioning.Tests.Modules;

/// <summary>
/// Mirrors the sudo-promotion cases formerly locked by
/// <c>UsersGroupsModuleTests</c>, now that the decision lives in the shared
/// <see cref="SudoPolicy"/> consumed by both UsersGroupsModule and
/// <see cref="DefaultUserResolver"/>.
/// </summary>
public sealed class SudoPolicyTests
{
    [Theory]
    [InlineData("ALL")]
    [InlineData("ALL=(ALL) NOPASSWD:ALL")]
    [InlineData("true")]
    public void Truthy_entry_enables_sudo(string sudo)
    {
        SudoPolicy.IsSudoEnabled([sudo]).Should().BeTrue();
    }

    [Theory]
    [InlineData("false")]
    [InlineData("FALSE")]
    [InlineData(" false ")]
    public void False_only_entry_does_not_enable_sudo(string sudo)
    {
        SudoPolicy.IsSudoEnabled([sudo]).Should().BeFalse();
    }

    [Fact]
    public void Mixed_list_with_one_truthy_entry_enables_sudo()
    {
        SudoPolicy.IsSudoEnabled(["ALL=(ALL) NOPASSWD:ALL", "false"]).Should().BeTrue();
    }

    [Fact]
    public void Null_disables_sudo()
    {
        SudoPolicy.IsSudoEnabled(null).Should().BeFalse();
    }

    [Fact]
    public void Empty_list_disables_sudo()
    {
        SudoPolicy.IsSudoEnabled([]).Should().BeFalse();
    }

    [Fact]
    public void Whitespace_only_entries_disable_sudo()
    {
        SudoPolicy.IsSudoEnabled(["", "   "]).Should().BeFalse();
    }
}
