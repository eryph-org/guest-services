using AwesomeAssertions;
using Eryph.GuestServices.Service.Services;

namespace Eryph.GuestServices.Service.Tests;

public class CloudInitStateMapperTests
{
    [Theory]
    [InlineData("done", "completed")]
    [InlineData("error", "failed")]
    [InlineData("running", "running")]
    [InlineData("disabled", "completed")]
    public void Map_translates_cloud_init_status_to_provisioning_state(string status, string expected)
    {
        CloudInitStateMapper.Map(status).Should().Be(expected);
    }

    [Theory]
    [InlineData("not run")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("something-new")]
    public void Map_returns_null_when_there_is_nothing_to_report(string? status)
    {
        CloudInitStateMapper.Map(status).Should().BeNull();
    }

    [Theory]
    [InlineData("done", true)]
    [InlineData("error", true)]
    [InlineData("disabled", true)]
    [InlineData("running", false)]
    [InlineData("not run", false)]
    public void IsTerminal_is_true_only_for_finished_states(string status, bool expected)
    {
        CloudInitStateMapper.IsTerminal(status).Should().Be(expected);
    }
}
