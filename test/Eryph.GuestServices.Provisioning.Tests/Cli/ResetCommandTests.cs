using AwesomeAssertions;
using Eryph.GuestServices.Provisioning.Cli;

namespace Eryph.GuestServices.Provisioning.Tests.Cli;

/// <summary>
/// The ResetCommand deletes well-known paths under <c>%ProgramData%\eryph\provisioning</c>.
/// Tests redirect that root to a temp directory via <see cref="ProvisioningPaths.RootOverride"/>
/// so we never touch the real install.
/// </summary>
[Collection(nameof(ProvisioningPathsCollection))]
public sealed class ResetCommandTests : IDisposable
{
    private readonly string _root;

    public ResetCommandTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "egs-reset-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        ProvisioningPaths.RootOverride = _root;
    }

    public void Dispose()
    {
        ProvisioningPaths.RootOverride = null;
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task ExecuteAsync_no_state_file_returns_zero()
    {
        var sut = new ResetCommand();

        var exit = await sut.ExecuteAsync(
            TestCommandContext.Create("reset"),
            new ResetCommand.Settings { Yes = true });

        exit.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_deletes_existing_state_file()
    {
        var stateFile = Path.Combine(_root, "state.json");
        await File.WriteAllTextAsync(stateFile, "{}");

        var sut = new ResetCommand();
        var exit = await sut.ExecuteAsync(
            TestCommandContext.Create("reset"),
            new ResetCommand.Settings { Yes = true });

        exit.Should().Be(0);
        File.Exists(stateFile).Should().BeFalse();
    }

    // Regression: an earlier version prompted for confirmation unless --yes was
    // passed, which broke non-TTY callers (Pester, CI). Reset must succeed without
    // --yes, matching cloud-init's `clean` semantics.
    [Fact]
    public async Task ExecuteAsync_without_yes_flag_still_deletes_in_non_interactive()
    {
        var stateFile = Path.Combine(_root, "state.json");
        await File.WriteAllTextAsync(stateFile, "{}");

        var sut = new ResetCommand();
        var exit = await sut.ExecuteAsync(
            TestCommandContext.Create("reset"),
            new ResetCommand.Settings { Yes = false });

        exit.Should().Be(0);
        File.Exists(stateFile).Should().BeFalse();
    }

    // Regression: Pester drives reset with --state-dir to point at a temp dir
    // so the real install state stays untouched. Verify the flag routes through
    // to ProvisioningPaths.
    [Fact]
    public async Task ExecuteAsync_StateDirFlag_OverridesRoot()
    {
        // Clear any test-set override so we're sure it's the flag doing the work.
        ProvisioningPaths.RootOverride = null;

        var altRoot = Path.Combine(Path.GetTempPath(), "egs-reset-altroot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(altRoot);
        try
        {
            var stateFile = Path.Combine(altRoot, "state.json");
            await File.WriteAllTextAsync(stateFile, "{}");

            var sut = new ResetCommand();
            var exit = await sut.ExecuteAsync(
                TestCommandContext.Create("reset"),
                new ResetCommand.Settings { StateDir = altRoot });

            exit.Should().Be(0);
            File.Exists(stateFile).Should().BeFalse();
        }
        finally
        {
            ProvisioningPaths.RootOverride = _root;
            if (Directory.Exists(altRoot))
                Directory.Delete(altRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_with_logs_deletes_logs_directory()
    {
        var logsDir = Path.Combine(_root, "logs");
        Directory.CreateDirectory(logsDir);
        await File.WriteAllTextAsync(Path.Combine(logsDir, "agent.log"), "hello");

        var sut = new ResetCommand();
        var exit = await sut.ExecuteAsync(
            TestCommandContext.Create("reset"),
            new ResetCommand.Settings { Yes = true, ClearLogs = true });

        exit.Should().Be(0);
        Directory.Exists(logsDir).Should().BeFalse();
    }

    // RFC 0010: reset wipes per-instance + state.json + per-boot, but keeps
    // per-once unless --reset-once is passed. This matches cloud-init's
    // `cloud-init clean` defaults.
    [Fact]
    public async Task ExecuteAsync_PerOnce_SurvivesDefaultReset()
    {
        var globalSem = Path.Combine(_root, "sem");
        Directory.CreateDirectory(globalSem);
        var perOnceMarker = Path.Combine(globalSem, "Mod.per-once");
        var perBootMarker = Path.Combine(globalSem, "Mod.per-boot");
        await File.WriteAllTextAsync(perOnceMarker, "{}");
        await File.WriteAllTextAsync(perBootMarker, "{}");

        var sut = new ResetCommand();
        var exit = await sut.ExecuteAsync(
            TestCommandContext.Create("reset"),
            new ResetCommand.Settings { Yes = true });

        exit.Should().Be(0);
        File.Exists(perBootMarker).Should().BeFalse("per-boot is transient and cleared by default");
        File.Exists(perOnceMarker).Should().BeTrue("per-once survives default reset");
    }

    [Fact]
    public async Task ExecuteAsync_ResetOnce_RemovesPerOnceMarkers()
    {
        var globalSem = Path.Combine(_root, "sem");
        Directory.CreateDirectory(globalSem);
        var perOnceMarker = Path.Combine(globalSem, "Mod.per-once");
        await File.WriteAllTextAsync(perOnceMarker, "{}");

        var sut = new ResetCommand();
        var exit = await sut.ExecuteAsync(
            TestCommandContext.Create("reset"),
            new ResetCommand.Settings { Yes = true, ResetOnce = true });

        exit.Should().Be(0);
        File.Exists(perOnceMarker).Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_KeepPerBoot_LeavesPerBootMarkersInPlace()
    {
        var globalSem = Path.Combine(_root, "sem");
        Directory.CreateDirectory(globalSem);
        var perBootMarker = Path.Combine(globalSem, "Mod.per-boot");
        await File.WriteAllTextAsync(perBootMarker, "{}");

        var sut = new ResetCommand();
        var exit = await sut.ExecuteAsync(
            TestCommandContext.Create("reset"),
            new ResetCommand.Settings { Yes = true, KeepPerBoot = true });

        exit.Should().Be(0);
        File.Exists(perBootMarker).Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_DeletesPerInstanceDirectory()
    {
        var instanceDir = Path.Combine(_root, "instance", "i-1", "sem");
        Directory.CreateDirectory(instanceDir);
        await File.WriteAllTextAsync(Path.Combine(instanceDir, "Mod.per-instance"), "{}");

        var sut = new ResetCommand();
        var exit = await sut.ExecuteAsync(
            TestCommandContext.Create("reset"),
            new ResetCommand.Settings { Yes = true });

        exit.Should().Be(0);
        Directory.Exists(Path.Combine(_root, "instance")).Should().BeFalse();
    }
}
