using AwesomeAssertions;
using Eryph.GuestServices.CloudConfig;
using Eryph.GuestServices.Core;
using Eryph.GuestServices.Provisioning.Update;
using Eryph.GuestServices.Service.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Eryph.GuestServices.Service.Tests;

public sealed class AutoUpdateServiceTests
{
    [Fact]
    public void NextCheckDelay_is_always_within_the_36_to_48_hour_window()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var rng = new Random(12345);
        for (var i = 0; i < 1000; i++)
        {
            var delay = AutoUpdateService.NextCheckDelay(rng);
            delay.Should().BeGreaterThanOrEqualTo(AutoUpdateService.MinInterval);
            delay.Should().BeLessThanOrEqualTo(AutoUpdateService.MaxInterval);
        }
    }

    [Fact]
    public async Task Disabled_does_not_check_for_updates()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var updater = new FakeUpdater(_ => null);
        var service = Create(autoUpdateEnabled: false, updater, new FakeLauncher());

        await service.StartAsync(CancellationToken.None);
        if (service.ExecuteTask is not null)
            await service.ExecuteTask;

        updater.Calls.Should().Be(0);
    }

    [Fact]
    public async Task CheckOnce_applies_a_staged_plan_via_the_launcher()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var plan = new UpdatePlan { StagingDirectory = @"C:\stage\payload", TargetVersion = "0.4.0" };
        var launcher = new FakeLauncher();
        var service = Create(autoUpdateEnabled: true, new FakeUpdater(_ => plan), launcher);

        var applied = await service.CheckOnceAsync(CancellationToken.None);

        applied.Should().BeTrue();
        launcher.Launched.Should().ContainSingle().Which.Should().BeSameAs(plan);
    }

    [Fact]
    public async Task CheckOnce_does_nothing_when_already_current()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var launcher = new FakeLauncher();
        var service = Create(autoUpdateEnabled: true, new FakeUpdater(_ => null), launcher);

        var applied = await service.CheckOnceAsync(CancellationToken.None);

        applied.Should().BeFalse();
        launcher.Launched.Should().BeEmpty();
    }

    [Fact]
    public async Task CheckOnce_swallows_a_transient_failure()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var launcher = new FakeLauncher();
        var service = Create(
            autoUpdateEnabled: true,
            new FakeUpdater(_ => throw new HttpRequestException("offline")),
            launcher);

        var applied = await service.CheckOnceAsync(CancellationToken.None);

        applied.Should().BeFalse();
        launcher.Launched.Should().BeEmpty();
    }

    private static AutoUpdateService Create(
        bool autoUpdateEnabled, IEgsUpdater updater, IUpdateLauncher launcher) =>
        new(new FakeFlags(autoUpdateEnabled), updater, launcher, NullLogger<AutoUpdateService>.Instance);

    private sealed class FakeFlags(bool autoUpdate) : IServiceControlFlags
    {
        public bool IsProvisioningEnabled() => true;
        public bool IsRemoteAccessEnabled() => true;
        public bool IsKvpAuthEnabled() => true;
        public bool IsAutoUpdateEnabled() => autoUpdate;
        public bool IsPortForwardingEnabled() => false;
    }

    private sealed class FakeUpdater(Func<EgsUpdateConfig?, UpdatePlan?> behavior) : IEgsUpdater
    {
        public int Calls { get; private set; }

        public Task<UpdatePlan?> PrepareAsync(EgsUpdateConfig? config, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(behavior(config));
        }
    }

    private sealed class FakeLauncher : IUpdateLauncher
    {
        public List<UpdatePlan> Launched { get; } = [];

        public void Launch(UpdatePlan plan) => Launched.Add(plan);
    }
}
