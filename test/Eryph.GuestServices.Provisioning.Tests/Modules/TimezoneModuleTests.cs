using System.Runtime.Versioning;
using AwesomeAssertions;
using Eryph.GuestServices.Provisioning.Modules;
using Eryph.GuestServices.Provisioning.UserData;
using Eryph.GuestServices.Provisioning.Windows;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using CloudConfigModel = global::Eryph.GuestServices.CloudConfig.CloudConfig;

namespace Eryph.GuestServices.Provisioning.Tests.Modules;

[SupportedOSPlatform("windows")]
public sealed class TimezoneModuleTests
{
    [Fact]
    public async Task Missing_timezone_skips_OS_call()
    {
        var os = Substitute.For<IWindowsOs>();
        var module = new TimezoneModule(NullLogger<TimezoneModule>.Instance);

        var result = await module.ApplyAsync(
            ResolvedUserData.Empty(new CloudConfigModel()),
            new TestModuleContext(os),
            CancellationToken.None);

        result.Should().BeOfType<ModuleOutcome.Completed>();
        await os.DidNotReceive().SetTimezoneAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IANA_timezone_is_translated_to_Windows_id()
    {
        // .NET ships the CLDR mapping in the runtime — Europe/Berlin always
        // resolves to "W. Europe Standard Time" regardless of host locale.
        var os = Substitute.For<IWindowsOs>();
        var module = new TimezoneModule(NullLogger<TimezoneModule>.Instance);

        var result = await module.ApplyAsync(
            ResolvedUserData.Empty(new CloudConfigModel { Timezone = "Europe/Berlin" }),
            new TestModuleContext(os),
            CancellationToken.None);

        result.Should().BeOfType<ModuleOutcome.Completed>();
        await os.Received(1).SetTimezoneAsync(
            "W. Europe Standard Time", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Windows_id_is_accepted_verbatim()
    {
        // Operators familiar with Windows can pass the Windows id directly —
        // we shouldn't reject it just because it isn't in IANA form.
        var os = Substitute.For<IWindowsOs>();
        var module = new TimezoneModule(NullLogger<TimezoneModule>.Instance);

        await module.ApplyAsync(
            ResolvedUserData.Empty(new CloudConfigModel { Timezone = "Pacific Standard Time" }),
            new TestModuleContext(os),
            CancellationToken.None);

        await os.Received(1).SetTimezoneAsync(
            "Pacific Standard Time", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Unknown_timezone_returns_failed_without_calling_OS()
    {
        var os = Substitute.For<IWindowsOs>();
        var module = new TimezoneModule(NullLogger<TimezoneModule>.Instance);

        var result = await module.ApplyAsync(
            ResolvedUserData.Empty(new CloudConfigModel { Timezone = "Mars/Olympus_Mons" }),
            new TestModuleContext(os),
            CancellationToken.None);

        var failed = result.Should().BeOfType<ModuleOutcome.Failed>().Subject;
        failed.Reason.Should().Contain("Mars/Olympus_Mons");
        await os.DidNotReceive().SetTimezoneAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OS_exception_surfaces_as_module_failed()
    {
        var os = Substitute.For<IWindowsOs>();
        os.SetTimezoneAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsyncForAnyArgs(new InvalidOperationException("tzutil exit 1"));

        var module = new TimezoneModule(NullLogger<TimezoneModule>.Instance);
        var result = await module.ApplyAsync(
            ResolvedUserData.Empty(new CloudConfigModel { Timezone = "UTC" }),
            new TestModuleContext(os),
            CancellationToken.None);

        result.Should().BeOfType<ModuleOutcome.Failed>()
            .Which.Reason.Should().Contain("tzutil");
    }
}
