using AwesomeAssertions;
using Eryph.GuestServices.CloudConfig;
using Eryph.GuestServices.Provisioning.Modules;
using Eryph.GuestServices.Provisioning.Stages;
using Eryph.GuestServices.Provisioning.UserData;
using Eryph.GuestServices.Provisioning.Windows;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using CloudConfigModel = global::Eryph.GuestServices.CloudConfig.CloudConfig;

namespace Eryph.GuestServices.Provisioning.Tests.Modules;

public sealed class GrowpartModuleTests
{
    [Fact]
    public async Task Default_targets_only_the_system_drive_when_growpart_is_absent()
    {
        // Cloud-init's documented default is devices: ['/'] which resolves
        // to the Windows system drive (typically C:). The same behaviour
        // must hold when the user omits the growpart key entirely.
        var os = Substitute.For<IWindowsOs>();
        os.ExtendVolumesAsync(Arg.Any<IReadOnlySet<char>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<VolumeExtendResult>>([]));

        var module = new GrowpartModule(NullLogger<GrowpartModule>.Instance);
        var result = await module.ApplyAsync(
            ResolvedUserData.Empty(new CloudConfigModel()),
            new TestModuleContext(os),
            CancellationToken.None);

        result.Should().BeOfType<ModuleOutcome.Completed>();
        var sysLetter = (Environment.GetEnvironmentVariable("SystemDrive") ?? "C:")[0];
        sysLetter = char.ToUpperInvariant(sysLetter);
        await os.Received(1).ExtendVolumesAsync(
            Arg.Is<IReadOnlySet<char>?>(s => s != null && s.Contains(sysLetter) && s.Count == 1),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Mode_off_skips_the_extend_call_entirely()
    {
        var os = Substitute.For<IWindowsOs>();
        var module = new GrowpartModule(NullLogger<GrowpartModule>.Instance);
        var config = new CloudConfigModel
        {
            Growpart = new GrowpartConfig { Mode = "off" },
        };

        var result = await module.ApplyAsync(
            ResolvedUserData.Empty(config),
            new TestModuleContext(os),
            CancellationToken.None);

        result.Should().BeOfType<ModuleOutcome.Completed>();
        await os.DidNotReceive().ExtendVolumesAsync(
            Arg.Any<IReadOnlySet<char>?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Mode_false_is_an_alias_for_off()
    {
        // cloud-init accepts both `off` and the boolean `false` to disable.
        var os = Substitute.For<IWindowsOs>();
        var module = new GrowpartModule(NullLogger<GrowpartModule>.Instance);
        var config = new CloudConfigModel
        {
            Growpart = new GrowpartConfig { Mode = "false" },
        };

        var result = await module.ApplyAsync(
            ResolvedUserData.Empty(config),
            new TestModuleContext(os),
            CancellationToken.None);

        result.Should().BeOfType<ModuleOutcome.Completed>();
        await os.DidNotReceive().ExtendVolumesAsync(
            Arg.Any<IReadOnlySet<char>?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Devices_with_all_passes_a_null_filter_meaning_grow_everything()
    {
        var os = Substitute.For<IWindowsOs>();
        os.ExtendVolumesAsync(Arg.Any<IReadOnlySet<char>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<VolumeExtendResult>>([]));

        var module = new GrowpartModule(NullLogger<GrowpartModule>.Instance);
        var config = new CloudConfigModel
        {
            Growpart = new GrowpartConfig { Devices = ["all"] },
        };

        var result = await module.ApplyAsync(
            ResolvedUserData.Empty(config),
            new TestModuleContext(os),
            CancellationToken.None);

        result.Should().BeOfType<ModuleOutcome.Completed>();
        // null filter ≡ extend every growable volume.
        await os.Received(1).ExtendVolumesAsync(
            Arg.Is<IReadOnlySet<char>?>(s => s == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Devices_with_drive_letters_resolves_to_an_uppercase_filter_set()
    {
        // Accepted shapes: "C:", "D:\\", lowercase "e", trailing slash.
        var os = Substitute.For<IWindowsOs>();
        os.ExtendVolumesAsync(Arg.Any<IReadOnlySet<char>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<VolumeExtendResult>>([]));

        var module = new GrowpartModule(NullLogger<GrowpartModule>.Instance);
        var config = new CloudConfigModel
        {
            Growpart = new GrowpartConfig { Devices = ["C:", "D:\\", "e"] },
        };

        await module.ApplyAsync(
            ResolvedUserData.Empty(config),
            new TestModuleContext(os),
            CancellationToken.None);

        await os.Received(1).ExtendVolumesAsync(
            Arg.Is<IReadOnlySet<char>?>(s =>
                s != null && s.Count == 3 && s.Contains('C') && s.Contains('D') && s.Contains('E')),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Slash_alias_resolves_to_the_system_drive()
    {
        // Validates the cloud-init `/` → %SystemDrive% translation in
        // isolation from the empty-config "default to ['/']" branch.
        var os = Substitute.For<IWindowsOs>();
        os.ExtendVolumesAsync(Arg.Any<IReadOnlySet<char>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<VolumeExtendResult>>([]));

        var module = new GrowpartModule(NullLogger<GrowpartModule>.Instance);
        var config = new CloudConfigModel
        {
            Growpart = new GrowpartConfig { Devices = ["/"] },
        };

        await module.ApplyAsync(
            ResolvedUserData.Empty(config),
            new TestModuleContext(os),
            CancellationToken.None);

        var sysLetter = char.ToUpperInvariant((Environment.GetEnvironmentVariable("SystemDrive") ?? "C:")[0]);
        await os.Received(1).ExtendVolumesAsync(
            Arg.Is<IReadOnlySet<char>?>(s => s != null && s.Count == 1 && s.Contains(sysLetter)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Unrecognised_devices_entries_result_in_an_empty_filter_and_a_no_op()
    {
        // Garbage device strings should not be misinterpreted as "extend
        // every drive" — that would be the most dangerous misreading.
        var os = Substitute.For<IWindowsOs>();
        var module = new GrowpartModule(NullLogger<GrowpartModule>.Instance);
        var config = new CloudConfigModel
        {
            Growpart = new GrowpartConfig { Devices = ["nonsense", ""] },
        };

        var result = await module.ApplyAsync(
            ResolvedUserData.Empty(config),
            new TestModuleContext(os),
            CancellationToken.None);

        result.Should().BeOfType<ModuleOutcome.Completed>();
        // Neither a filtered nor a null-filtered call must happen.
        await os.DidNotReceive().ExtendVolumesAsync(
            Arg.Any<IReadOnlySet<char>?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Unsupported_mode_warns_and_is_a_no_op()
    {
        // cloud-init's "growpart" / "gpart" modes are Linux-specific. We
        // refuse to silently behave like "auto" — operator must explicitly
        // opt in with mode: auto.
        var os = Substitute.For<IWindowsOs>();
        var module = new GrowpartModule(NullLogger<GrowpartModule>.Instance);
        var config = new CloudConfigModel
        {
            Growpart = new GrowpartConfig { Mode = "growpart" },
        };

        var result = await module.ApplyAsync(
            ResolvedUserData.Empty(config),
            new TestModuleContext(os),
            CancellationToken.None);

        result.Should().BeOfType<ModuleOutcome.Completed>();
        await os.DidNotReceive().ExtendVolumesAsync(
            Arg.Any<IReadOnlySet<char>?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OS_exception_surfaces_as_module_failed()
    {
        // A throw out of the storage CIM layer must not crash the stage
        // runner — it surfaces as ModuleOutcome.Failed so the dispatcher
        // emits ProvisioningFailed and re-runs the module on the next pass
        // (Failed deliberately writes no semaphore).
        var os = Substitute.For<IWindowsOs>();
        os.ExtendVolumesAsync(Arg.Any<IReadOnlySet<char>?>(), Arg.Any<CancellationToken>())
            .ThrowsAsyncForAnyArgs(new InvalidOperationException("CIM blew up"));

        var module = new GrowpartModule(NullLogger<GrowpartModule>.Instance);
        var result = await module.ApplyAsync(
            ResolvedUserData.Empty(new CloudConfigModel()),
            new TestModuleContext(os),
            CancellationToken.None);

        var failed = result.Should().BeOfType<ModuleOutcome.Failed>().Subject;
        failed.Reason.Should().Contain("CIM blew up");
    }

    [Fact]
    public async Task Cancellation_is_propagated_without_being_wrapped()
    {
        // OperationCanceledException must NOT be swallowed into a Failed
        // outcome — the stage runner relies on the original exception type
        // to distinguish operator cancel from module failure.
        var os = Substitute.For<IWindowsOs>();
        os.ExtendVolumesAsync(Arg.Any<IReadOnlySet<char>?>(), Arg.Any<CancellationToken>())
            .ThrowsAsyncForAnyArgs(new OperationCanceledException());

        var module = new GrowpartModule(NullLogger<GrowpartModule>.Instance);
        Func<Task> act = () => module.ApplyAsync(
            ResolvedUserData.Empty(new CloudConfigModel()),
            new TestModuleContext(os),
            CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void Stage_attribute_is_Network_Order_0_PerBoot()
    {
        // The frequency is the part that broke for cloudbase-init: per-instance
        // would skip the module after the first boot, so an operator who
        // resized the VHD between reboots would see nothing happen. Pin the
        // exact values here so a refactor doesn't silently regress them.
        var attr = typeof(GrowpartModule)
            .GetCustomAttributes(typeof(StageAttribute), inherit: false)
            .OfType<StageAttribute>()
            .Single();

        attr.Stage.Should().Be(Stage.Network);
        attr.Order.Should().Be(0);
        attr.Frequency.Should().Be(ModuleFrequency.PerBoot);
    }
}
