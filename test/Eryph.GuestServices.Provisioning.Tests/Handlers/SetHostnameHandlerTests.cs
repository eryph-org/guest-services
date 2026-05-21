using AwesomeAssertions;
using Eryph.GuestServices.Provisioning.Handlers;
using Eryph.GuestServices.Provisioning.Stages;
using Eryph.GuestServices.Provisioning.Windows;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using CloudConfigModel = global::Eryph.GuestServices.CloudConfig.CloudConfig;

namespace Eryph.GuestServices.Provisioning.Tests.Handlers;

public sealed class SetHostnameHandlerTests
{
    [Fact]
    public async Task Returns_completed_when_both_hostname_and_fqdn_are_null()
    {
        var os = Substitute.For<IWindowsOs>();
        var handler = new SetHostnameHandler(NullLogger<SetHostnameHandler>.Instance);

        var result = await handler.ApplyAsync(new CloudConfigModel(), new TestHandlerContext(os), CancellationToken.None);

        result.Should().BeOfType<HandlerOutcome.Completed>();
        await os.DidNotReceive().SetComputerNameAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Returns_completed_when_preserve_hostname_is_true()
    {
        var os = Substitute.For<IWindowsOs>();
        var handler = new SetHostnameHandler(NullLogger<SetHostnameHandler>.Instance);

        var config = new CloudConfigModel { Hostname = "ignored", PreserveHostname = true };
        var result = await handler.ApplyAsync(config, new TestHandlerContext(os), CancellationToken.None);

        result.Should().BeOfType<HandlerOutcome.Completed>();
        await os.DidNotReceive().SetComputerNameAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Uses_hostname_when_set()
    {
        var os = Substitute.For<IWindowsOs>();
        os.SetComputerNameAsync("box", Arg.Any<CancellationToken>())
            .Returns(SetComputerNameResult.SetWithRebootPending);
        var handler = new SetHostnameHandler(NullLogger<SetHostnameHandler>.Instance);

        var result = await handler.ApplyAsync(
            new CloudConfigModel { Hostname = "box" }, new TestHandlerContext(os), CancellationToken.None);

        result.Should().BeOfType<HandlerOutcome.RebootRequested>();
        await os.Received().SetComputerNameAsync("box", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Falls_back_to_first_label_of_fqdn()
    {
        var os = Substitute.For<IWindowsOs>();
        os.SetComputerNameAsync("box", Arg.Any<CancellationToken>())
            .Returns(SetComputerNameResult.Set);
        var handler = new SetHostnameHandler(NullLogger<SetHostnameHandler>.Instance);

        var result = await handler.ApplyAsync(
            new CloudConfigModel { Fqdn = "box.example.com" }, new TestHandlerContext(os), CancellationToken.None);

        result.Should().BeOfType<HandlerOutcome.Completed>();
        await os.Received().SetComputerNameAsync("box", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Returns_completed_when_name_is_already_set()
    {
        var os = Substitute.For<IWindowsOs>();
        os.SetComputerNameAsync("box", Arg.Any<CancellationToken>())
            .Returns(SetComputerNameResult.AlreadySet);
        var handler = new SetHostnameHandler(NullLogger<SetHostnameHandler>.Instance);

        var result = await handler.ApplyAsync(
            new CloudConfigModel { Hostname = "box" }, new TestHandlerContext(os), CancellationToken.None);

        result.Should().BeOfType<HandlerOutcome.Completed>();
    }
}
