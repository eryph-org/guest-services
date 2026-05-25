using AwesomeAssertions;
using Eryph.GuestServices.Provisioning.Cli;
using Eryph.GuestServices.Provisioning.State;
using NSubstitute;

namespace Eryph.GuestServices.Provisioning.Tests.Cli;

public sealed class StatusCommandTests
{
    [Fact]
    public async Task ExecuteAsync_returns_zero_when_no_state_present()
    {
        var store = Substitute.For<IStateStore>();
        store.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ProvisioningState?>(null));
        var sut = new StatusCommand(store);

        var exit = await sut.ExecuteAsync(TestCommandContext.Create("status"), new StatusCommand.Settings());

        exit.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_returns_zero_with_in_progress_state()
    {
        var store = Substitute.For<IStateStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(
            Task.FromResult<ProvisioningState?>(new ProvisioningState
            {
                InstanceId = "i-1",
                CompletedStages = ["Local", "Network"],
                CompletedHandlers = ["NS.HostnameModule"],
            }));
        var sut = new StatusCommand(store);

        var exit = await sut.ExecuteAsync(TestCommandContext.Create("status"), new StatusCommand.Settings());

        exit.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_emits_json_when_requested()
    {
        var store = Substitute.For<IStateStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(
            Task.FromResult<ProvisioningState?>(new ProvisioningState { InstanceId = "i-1" }));
        var sut = new StatusCommand(store);

        var exit = await sut.ExecuteAsync(
            TestCommandContext.Create("status"),
            new StatusCommand.Settings { Json = true });

        exit.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_wait_returns_when_state_reaches_Final()
    {
        var store = Substitute.For<IStateStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(
            Task.FromResult<ProvisioningState?>(new ProvisioningState
            {
                InstanceId = "i-1",
                CompletedStages = ["Local", "Network", "Config", "Final"],
            }));
        var sut = new StatusCommand(store);

        var exit = await sut.ExecuteAsync(
            TestCommandContext.Create("status"),
            new StatusCommand.Settings { Wait = true });

        exit.Should().Be(0);
        // The wait loop polls until terminal; with Final present the first
        // load satisfies the condition immediately.
        await store.Received().LoadAsync(Arg.Any<CancellationToken>());
    }
}
