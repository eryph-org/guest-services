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

    // Cloud-init's prefer_fqdn_over_hostname swaps the precedence: with
    // the flag set, the fqdn wins even if hostname is also defined.
    [Fact]
    public async Task Prefer_fqdn_picks_fqdn_first_label_over_hostname()
    {
        var os = Substitute.For<IWindowsOs>();
        os.SetComputerNameAsync("b", Arg.Any<CancellationToken>())
            .Returns(SetComputerNameResult.AlreadySet);
        var module = new SetHostnameModule(NullLogger<SetHostnameModule>.Instance);

        var config = new CloudConfigModel
        {
            Hostname = "a",
            Fqdn = "b.example.com",
            PreferFqdnOverHostname = true,
        };

        await module.ApplyAsync(
            ResolvedUserData.Empty(config),
            new TestModuleContext(os),
            CancellationToken.None);

        await os.Received().SetComputerNameAsync("b", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Prefer_fqdn_false_falls_back_to_hostname_first_precedence()
    {
        var os = Substitute.For<IWindowsOs>();
        os.SetComputerNameAsync("a", Arg.Any<CancellationToken>())
            .Returns(SetComputerNameResult.AlreadySet);
        var module = new SetHostnameModule(NullLogger<SetHostnameModule>.Instance);

        var config = new CloudConfigModel
        {
            Hostname = "a",
            Fqdn = "b.example.com",
            PreferFqdnOverHostname = false,
        };

        await module.ApplyAsync(
            ResolvedUserData.Empty(config),
            new TestModuleContext(os),
            CancellationToken.None);

        await os.Received().SetComputerNameAsync("a", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Prefer_fqdn_without_fqdn_falls_through_to_hostname()
    {
        var os = Substitute.For<IWindowsOs>();
        os.SetComputerNameAsync("a", Arg.Any<CancellationToken>())
            .Returns(SetComputerNameResult.AlreadySet);
        var module = new SetHostnameModule(NullLogger<SetHostnameModule>.Instance);

        var config = new CloudConfigModel
        {
            Hostname = "a",
            PreferFqdnOverHostname = true,
        };

        await module.ApplyAsync(
            ResolvedUserData.Empty(config),
            new TestModuleContext(os),
            CancellationToken.None);

        await os.Received().SetComputerNameAsync("a", Arg.Any<CancellationToken>());
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
