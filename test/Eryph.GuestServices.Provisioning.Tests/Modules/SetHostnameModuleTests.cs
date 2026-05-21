using AwesomeAssertions;
using Eryph.GuestServices.Provisioning.Modules;
using Eryph.GuestServices.Provisioning.UserData;
using Eryph.GuestServices.Provisioning.Windows;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using CloudConfigModel = global::Eryph.GuestServices.CloudConfig.CloudConfig;

namespace Eryph.GuestServices.Provisioning.Tests.Modules;

public sealed class SetHostnameModuleTests
{
    [Fact]
    public async Task Returns_completed_when_both_hostname_and_fqdn_are_null()
    {
        var os = Substitute.For<IWindowsOs>();
        var module = new SetHostnameModule(NullLogger<SetHostnameModule>.Instance);

        var result = await module.ApplyAsync(
            ResolvedUserData.Empty(new CloudConfigModel()),
            new TestModuleContext(os),
            CancellationToken.None);

        result.Should().BeOfType<ModuleOutcome.Completed>();
        await os.DidNotReceive().SetComputerNameAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Returns_completed_when_preserve_hostname_is_true()
    {
        var os = Substitute.For<IWindowsOs>();
        var module = new SetHostnameModule(NullLogger<SetHostnameModule>.Instance);

        var config = new CloudConfigModel { Hostname = "ignored", PreserveHostname = true };
        var result = await module.ApplyAsync(
            ResolvedUserData.Empty(config),
            new TestModuleContext(os),
            CancellationToken.None);

        result.Should().BeOfType<ModuleOutcome.Completed>();
        await os.DidNotReceive().SetComputerNameAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Uses_hostname_when_set()
    {
        var os = Substitute.For<IWindowsOs>();
        os.SetComputerNameAsync("box", Arg.Any<CancellationToken>())
            .Returns(SetComputerNameResult.SetWithRebootPending);
        var module = new SetHostnameModule(NullLogger<SetHostnameModule>.Instance);

        var result = await module.ApplyAsync(
            ResolvedUserData.Empty(new CloudConfigModel { Hostname = "box" }),
            new TestModuleContext(os),
            CancellationToken.None);

        result.Should().BeOfType<ModuleOutcome.RebootRequested>();
        await os.Received().SetComputerNameAsync("box", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Falls_back_to_first_label_of_fqdn()
    {
        var os = Substitute.For<IWindowsOs>();
        os.SetComputerNameAsync("box", Arg.Any<CancellationToken>())
            .Returns(SetComputerNameResult.AlreadySet);
        var module = new SetHostnameModule(NullLogger<SetHostnameModule>.Instance);

        var result = await module.ApplyAsync(
            ResolvedUserData.Empty(new CloudConfigModel { Fqdn = "box.example.com" }),
            new TestModuleContext(os),
            CancellationToken.None);

        result.Should().BeOfType<ModuleOutcome.Completed>();
        await os.Received().SetComputerNameAsync("box", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Returns_completed_when_name_is_already_set()
    {
        var os = Substitute.For<IWindowsOs>();
        os.SetComputerNameAsync("box", Arg.Any<CancellationToken>())
            .Returns(SetComputerNameResult.AlreadySet);
        var module = new SetHostnameModule(NullLogger<SetHostnameModule>.Instance);

        var result = await module.ApplyAsync(
            ResolvedUserData.Empty(new CloudConfigModel { Hostname = "box" }),
            new TestModuleContext(os),
            CancellationToken.None);

        result.Should().BeOfType<ModuleOutcome.Completed>();
    }
}
