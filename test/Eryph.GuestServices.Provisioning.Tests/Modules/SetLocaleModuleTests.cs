using AwesomeAssertions;
using Eryph.GuestServices.CloudConfig;
using Eryph.GuestServices.Provisioning.Modules;
using Eryph.GuestServices.Provisioning.UserData;
using Eryph.GuestServices.Provisioning.Windows;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using CloudConfigModel = global::Eryph.GuestServices.CloudConfig.CloudConfig;

namespace Eryph.GuestServices.Provisioning.Tests.Modules;

public sealed class SetLocaleModuleTests
{
    [Fact]
    public async Task Both_absent_skips_OS_call()
    {
        var os = Substitute.For<IWindowsOs>();
        var module = new SetLocaleModule(NullLogger<SetLocaleModule>.Instance);

        var result = await module.ApplyAsync(
            ResolvedUserData.Empty(new CloudConfigModel()),
            new TestModuleContext(os),
            CancellationToken.None);

        result.Should().BeOfType<ModuleOutcome.Completed>();
        await os.DidNotReceive().ApplyLocaleAsync(
            Arg.Any<LocaleSpec>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Locale_only_forwards_with_null_keyboard()
    {
        var os = Substitute.For<IWindowsOs>();
        os.ApplyLocaleAsync(Arg.Any<LocaleSpec>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new LocaleApplyResult { RebootRequired = false }));
        var module = new SetLocaleModule(NullLogger<SetLocaleModule>.Instance);

        var result = await module.ApplyAsync(
            ResolvedUserData.Empty(new CloudConfigModel { Locale = "de-DE" }),
            new TestModuleContext(os),
            CancellationToken.None);

        result.Should().BeOfType<ModuleOutcome.Completed>();
        await os.Received(1).ApplyLocaleAsync(
            Arg.Is<LocaleSpec>(s => s.Locale == "de-DE" && s.KeyboardLayout == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Keyboard_only_forwards_with_null_locale()
    {
        // Keyboard-only is a valid scenario — operator wants QWERTZ but
        // leaves the display language at the host default.
        var os = Substitute.For<IWindowsOs>();
        os.ApplyLocaleAsync(Arg.Any<LocaleSpec>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new LocaleApplyResult { RebootRequired = false }));
        var module = new SetLocaleModule(NullLogger<SetLocaleModule>.Instance);

        var result = await module.ApplyAsync(
            ResolvedUserData.Empty(new CloudConfigModel
            {
                Keyboard = new KeyboardConfig { Layout = "0407:00000407" },
            }),
            new TestModuleContext(os),
            CancellationToken.None);

        result.Should().BeOfType<ModuleOutcome.Completed>();
        await os.Received(1).ApplyLocaleAsync(
            Arg.Is<LocaleSpec>(s => s.Locale == null && s.KeyboardLayout == "0407:00000407"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task System_locale_change_returns_RebootRequested()
    {
        // The OS layer signals reboot via LocaleApplyResult.RebootRequired
        // (Set-WinSystemLocale needs reboot to fully apply). The module must
        // translate that into ModuleOutcome.RebootRequested so the StageRunner
        // can persist the reboot-requested semaphore and resume after restart.
        var os = Substitute.For<IWindowsOs>();
        os.ApplyLocaleAsync(Arg.Any<LocaleSpec>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new LocaleApplyResult { RebootRequired = true }));
        var module = new SetLocaleModule(NullLogger<SetLocaleModule>.Instance);

        var result = await module.ApplyAsync(
            ResolvedUserData.Empty(new CloudConfigModel { Locale = "de-DE" }),
            new TestModuleContext(os),
            CancellationToken.None);

        var reboot = result.Should().BeOfType<ModuleOutcome.RebootRequested>().Subject;
        reboot.Reason.Should().Contain("de-DE");
    }

    [Fact]
    public async Task Whitespace_only_values_are_treated_as_absent()
    {
        // Catches "field present, content empty" — e.g. someone left a YAML
        // placeholder in the template.
        var os = Substitute.For<IWindowsOs>();
        var module = new SetLocaleModule(NullLogger<SetLocaleModule>.Instance);

        var result = await module.ApplyAsync(
            ResolvedUserData.Empty(new CloudConfigModel
            {
                Locale = "   ",
                Keyboard = new KeyboardConfig { Layout = "  " },
            }),
            new TestModuleContext(os),
            CancellationToken.None);

        result.Should().BeOfType<ModuleOutcome.Completed>();
        await os.DidNotReceive().ApplyLocaleAsync(
            Arg.Any<LocaleSpec>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OS_exception_surfaces_as_module_failed()
    {
        var os = Substitute.For<IWindowsOs>();
        os.ApplyLocaleAsync(Arg.Any<LocaleSpec>(), Arg.Any<CancellationToken>())
            .ThrowsAsyncForAnyArgs(new InvalidOperationException("Set-Culture exit 1"));
        var module = new SetLocaleModule(NullLogger<SetLocaleModule>.Instance);

        var result = await module.ApplyAsync(
            ResolvedUserData.Empty(new CloudConfigModel { Locale = "de-DE" }),
            new TestModuleContext(os),
            CancellationToken.None);

        result.Should().BeOfType<ModuleOutcome.Failed>()
            .Which.Reason.Should().Contain("Set-Culture");
    }
}
