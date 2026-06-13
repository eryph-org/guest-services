using AwesomeAssertions;
using Eryph.GuestServices.Provisioning.DataSources;
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

    // Regression: a 17-char hostname like "remote-access-e2e" exceeds the 15-char
    // Windows NetBIOS limit. Without truncation the OS layer compares the long
    // desired name against the post-rename, already-truncated Environment.MachineName
    // ("REMOTE-ACCESS-E"), the comparison never matches, and the module
    // requests a reboot on every run — reboot-looping until the cap kicks in.
    [Fact]
    public async Task Truncates_hostname_to_netbios_max_length()
    {
        var os = Substitute.For<IWindowsOs>();
        os.SetComputerNameAsync("remote-access-e", Arg.Any<CancellationToken>())
            .Returns(SetComputerNameResult.AlreadySet);
        var module = new SetHostnameModule(NullLogger<SetHostnameModule>.Instance);

        var result = await module.ApplyAsync(
            ResolvedUserData.Empty(new CloudConfigModel { Hostname = "remote-access-e2e" }),
            new TestModuleContext(os),
            CancellationToken.None);

        result.Should().BeOfType<ModuleOutcome.Completed>();
        await os.Received().SetComputerNameAsync("remote-access-e", Arg.Any<CancellationToken>());
        await os.DidNotReceive().SetComputerNameAsync("remote-access-e2e", Arg.Any<CancellationToken>());
    }

    // The same truncation must apply when the name is derived from the
    // first label of an fqdn — otherwise an fqdn whose first label is > 15
    // chars would still reboot-loop.
    [Fact]
    public async Task Truncates_fqdn_first_label_to_netbios_max_length()
    {
        var os = Substitute.For<IWindowsOs>();
        os.SetComputerNameAsync("remote-access-e", Arg.Any<CancellationToken>())
            .Returns(SetComputerNameResult.AlreadySet);
        var module = new SetHostnameModule(NullLogger<SetHostnameModule>.Instance);

        var result = await module.ApplyAsync(
            ResolvedUserData.Empty(new CloudConfigModel { Fqdn = "remote-access-e2e.example.com" }),
            new TestModuleContext(os),
            CancellationToken.None);

        result.Should().BeOfType<ModuleOutcome.Completed>();
        await os.Received().SetComputerNameAsync("remote-access-e", Arg.Any<CancellationToken>());
    }

    // One-shot guard: after the StageRunner re-enters us post-reboot the
    // module accepts whatever the OS settled on and completes. No second
    // rename attempt — even if Environment.MachineName disagrees with what
    // we requested for some reason we did not anticipate.
    [Fact]
    public async Task Reboot_resume_does_not_rename_again_even_when_machine_name_differs()
    {
        var os = Substitute.For<IWindowsOs>();
        os.GetComputerNameAsync(Arg.Any<CancellationToken>()).Returns("OS-NORMALIZED");
        var module = new SetHostnameModule(NullLogger<SetHostnameModule>.Instance);

        var result = await module.ApplyAsync(
            ResolvedUserData.Empty(new CloudConfigModel { Hostname = "some-other-name" }),
            new TestModuleContext(os, isRebootResume: true),
            CancellationToken.None);

        result.Should().BeOfType<ModuleOutcome.Completed>();
        await os.DidNotReceive().SetComputerNameAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Reboot_resume_completes_quietly_when_machine_name_matches()
    {
        var os = Substitute.For<IWindowsOs>();
        os.GetComputerNameAsync(Arg.Any<CancellationToken>()).Returns("box");
        var module = new SetHostnameModule(NullLogger<SetHostnameModule>.Instance);

        var result = await module.ApplyAsync(
            ResolvedUserData.Empty(new CloudConfigModel { Hostname = "box" }),
            new TestModuleContext(os, isRebootResume: true),
            CancellationToken.None);

        result.Should().BeOfType<ModuleOutcome.Completed>();
        await os.DidNotReceive().SetComputerNameAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
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

    // Regression for the "hostname never changes" bug: eryph delivers a
    // catlet's name as the datasource `local-hostname` (meta-data), not as a
    // cloud-config `hostname:`. With neither hostname nor fqdn in the
    // cloud-config, the module must fall back to the datasource hostname —
    // mirroring cloud-init's get_hostname_fqdn() fallback.
    [Fact]
    public async Task Uses_datasource_hostname_when_cloud_config_specifies_none()
    {
        var os = Substitute.For<IWindowsOs>();
        os.SetComputerNameAsync("egs-box", Arg.Any<CancellationToken>())
            .Returns(SetComputerNameResult.SetWithRebootPending);
        var module = new SetHostnameModule(NullLogger<SetHostnameModule>.Instance);

        var context = new TestModuleContext(os, new DataSourceResult
        {
            SourceName = "NoCloud",
            InstanceId = "i",
            Hostname = "egs-box",
        });

        var result = await module.ApplyAsync(
            ResolvedUserData.Empty(new CloudConfigModel()),
            context,
            CancellationToken.None);

        result.Should().BeOfType<ModuleOutcome.RebootRequested>();
        await os.Received().SetComputerNameAsync("egs-box", Arg.Any<CancellationToken>());
    }

    // The cloud-config hostname is higher precedence than the datasource
    // local-hostname (cloud-init: cfg['hostname'] wins over the datasource).
    [Fact]
    public async Task Cloud_config_hostname_wins_over_datasource_hostname()
    {
        var os = Substitute.For<IWindowsOs>();
        os.SetComputerNameAsync("from-config", Arg.Any<CancellationToken>())
            .Returns(SetComputerNameResult.AlreadySet);
        var module = new SetHostnameModule(NullLogger<SetHostnameModule>.Instance);

        var context = new TestModuleContext(os, new DataSourceResult
        {
            SourceName = "NoCloud",
            InstanceId = "i",
            Hostname = "from-datasource",
        });

        await module.ApplyAsync(
            ResolvedUserData.Empty(new CloudConfigModel { Hostname = "from-config" }),
            context,
            CancellationToken.None);

        await os.Received().SetComputerNameAsync("from-config", Arg.Any<CancellationToken>());
        await os.DidNotReceive().SetComputerNameAsync("from-datasource", Arg.Any<CancellationToken>());
    }

    // A dotted datasource hostname is reduced to its first NetBIOS label, the
    // same as a cloud-config fqdn.
    [Fact]
    public async Task Datasource_hostname_uses_first_netbios_label()
    {
        var os = Substitute.For<IWindowsOs>();
        os.SetComputerNameAsync("box", Arg.Any<CancellationToken>())
            .Returns(SetComputerNameResult.AlreadySet);
        var module = new SetHostnameModule(NullLogger<SetHostnameModule>.Instance);

        var context = new TestModuleContext(os, new DataSourceResult
        {
            SourceName = "NoCloud",
            InstanceId = "i",
            Hostname = "box.example.com",
        });

        var result = await module.ApplyAsync(
            ResolvedUserData.Empty(new CloudConfigModel()),
            context,
            CancellationToken.None);

        result.Should().BeOfType<ModuleOutcome.Completed>();
        await os.Received().SetComputerNameAsync("box", Arg.Any<CancellationToken>());
    }
}
