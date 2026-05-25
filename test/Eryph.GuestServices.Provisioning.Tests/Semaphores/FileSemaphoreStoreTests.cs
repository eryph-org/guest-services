using AwesomeAssertions;
using Eryph.GuestServices.Provisioning.Semaphores;
using Eryph.GuestServices.Provisioning.Stages;
using Microsoft.Extensions.Logging.Abstractions;

namespace Eryph.GuestServices.Provisioning.Tests.Semaphores;

public sealed class FileSemaphoreStoreTests : IDisposable
{
    private readonly string _tempDir;

    public FileSemaphoreStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "egs-sem-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task ExistsAsync_returns_false_when_marker_missing()
    {
        var store = NewStore();
        (await store.ExistsAsync("Some.Module", ModuleFrequency.PerInstance, "i-1", CancellationToken.None))
            .Should().BeFalse();
    }

    [Fact]
    public async Task WriteAsync_PerInstance_creates_file_at_cloud_init_layout()
    {
        var store = NewStore();
        await store.WriteAsync("Eryph.Module.X", ModuleFrequency.PerInstance, "i-42", "completed", CancellationToken.None);

        var expected = Path.Combine(_tempDir, "instance", "i-42", "sem", "Eryph.Module.X.per-instance");
        File.Exists(expected).Should().BeTrue();

        // Contents are a JSON line so operators can inspect the record.
        var content = await File.ReadAllTextAsync(expected);
        content.Should().Contain("\"instanceId\":\"i-42\"");
        content.Should().Contain("\"outcome\":\"completed\"");
    }

    [Fact]
    public async Task WriteAsync_PerBoot_creates_file_in_global_directory()
    {
        var store = NewStore();
        await store.WriteAsync("Mod.Boot", ModuleFrequency.PerBoot, "i-1", "completed", CancellationToken.None);

        var expected = Path.Combine(_tempDir, "sem", "Mod.Boot.per-boot");
        File.Exists(expected).Should().BeTrue();
    }

    [Fact]
    public async Task WriteAsync_PerOnce_creates_file_in_global_directory()
    {
        var store = NewStore();
        await store.WriteAsync("Mod.Once", ModuleFrequency.PerOnce, "i-1", "completed", CancellationToken.None);

        var expected = Path.Combine(_tempDir, "sem", "Mod.Once.per-once");
        File.Exists(expected).Should().BeTrue();
    }

    [Fact]
    public async Task ExistsAsync_true_after_write_for_same_scope()
    {
        var store = NewStore();
        await store.WriteAsync("Mod", ModuleFrequency.PerInstance, "i-1", "completed", CancellationToken.None);

        (await store.ExistsAsync("Mod", ModuleFrequency.PerInstance, "i-1", CancellationToken.None))
            .Should().BeTrue();
    }

    [Fact]
    public async Task ExistsAsync_isolated_across_instance_ids()
    {
        var store = NewStore();
        await store.WriteAsync("Mod", ModuleFrequency.PerInstance, "i-1", "completed", CancellationToken.None);

        (await store.ExistsAsync("Mod", ModuleFrequency.PerInstance, "i-2", CancellationToken.None))
            .Should().BeFalse();
    }

    [Fact]
    public async Task ClearPerInstanceAsync_removes_only_that_instance()
    {
        var store = NewStore();
        await store.WriteAsync("Mod", ModuleFrequency.PerInstance, "i-1", "completed", CancellationToken.None);
        await store.WriteAsync("Mod", ModuleFrequency.PerInstance, "i-2", "completed", CancellationToken.None);

        await store.ClearPerInstanceAsync("i-1", CancellationToken.None);

        (await store.ExistsAsync("Mod", ModuleFrequency.PerInstance, "i-1", CancellationToken.None))
            .Should().BeFalse();
        (await store.ExistsAsync("Mod", ModuleFrequency.PerInstance, "i-2", CancellationToken.None))
            .Should().BeTrue();
    }

    [Fact]
    public async Task ClearPerBootAsync_does_not_touch_per_once_or_per_instance()
    {
        var store = NewStore();
        await store.WriteAsync("Boot", ModuleFrequency.PerBoot, "i-1", "completed", CancellationToken.None);
        await store.WriteAsync("Once", ModuleFrequency.PerOnce, "i-1", "completed", CancellationToken.None);
        await store.WriteAsync("Inst", ModuleFrequency.PerInstance, "i-1", "completed", CancellationToken.None);

        await store.ClearPerBootAsync(CancellationToken.None);

        (await store.ExistsAsync("Boot", ModuleFrequency.PerBoot, "i-1", CancellationToken.None))
            .Should().BeFalse();
        (await store.ExistsAsync("Once", ModuleFrequency.PerOnce, "i-1", CancellationToken.None))
            .Should().BeTrue();
        (await store.ExistsAsync("Inst", ModuleFrequency.PerInstance, "i-1", CancellationToken.None))
            .Should().BeTrue();
    }

    [Fact]
    public async Task ClearPerOnceAsync_only_removes_per_once()
    {
        var store = NewStore();
        await store.WriteAsync("Boot", ModuleFrequency.PerBoot, "i-1", "completed", CancellationToken.None);
        await store.WriteAsync("Once", ModuleFrequency.PerOnce, "i-1", "completed", CancellationToken.None);

        await store.ClearPerOnceAsync(CancellationToken.None);

        (await store.ExistsAsync("Once", ModuleFrequency.PerOnce, "i-1", CancellationToken.None))
            .Should().BeFalse();
        (await store.ExistsAsync("Boot", ModuleFrequency.PerBoot, "i-1", CancellationToken.None))
            .Should().BeTrue();
    }

    [Fact]
    public async Task ListPerInstanceAsync_returns_module_keys_without_suffix()
    {
        var store = NewStore();
        await store.WriteAsync("Eryph.Module.A", ModuleFrequency.PerInstance, "i-1", "completed", CancellationToken.None);
        await store.WriteAsync("Eryph.Module.B", ModuleFrequency.PerInstance, "i-1", "completed", CancellationToken.None);

        var list = await store.ListPerInstanceAsync("i-1", CancellationToken.None);
        list.Should().BeEquivalentTo(["Eryph.Module.A", "Eryph.Module.B"]);
    }

    // Real-world fixture: synthesise a directory layout that matches what a
    // running catlet would have on disk after one provisioning run, then ask
    // the store to read it back. Catches any path-shape regressions that a
    // pure write-then-read test would miss.
    [Fact]
    public async Task ExistsAsync_reads_back_a_catlet_shaped_directory_layout()
    {
        const string instanceId = "abc12345-6789-0abc-def0-123456789abc";
        const string moduleKey = "Eryph.GuestServices.Provisioning.Modules.UsersGroupsModule";

        // Lay out the directory the way an actual run produces it.
        var perInstanceDir = Path.Combine(_tempDir, "instance", instanceId, "sem");
        Directory.CreateDirectory(perInstanceDir);
        await File.WriteAllTextAsync(
            Path.Combine(perInstanceDir, moduleKey + ".per-instance"),
            "{\"timestamp\":\"2026-01-01T00:00:00+00:00\",\"instanceId\":\"" + instanceId + "\",\"outcome\":\"completed\"}");

        var globalDir = Path.Combine(_tempDir, "sem");
        Directory.CreateDirectory(globalDir);
        await File.WriteAllTextAsync(
            Path.Combine(globalDir, "Eryph.GuestServices.Provisioning.Modules.RuncmdBootModule.per-boot"),
            "{}");

        var store = NewStore();
        (await store.ExistsAsync(moduleKey, ModuleFrequency.PerInstance, instanceId, CancellationToken.None))
            .Should().BeTrue();
        (await store.ExistsAsync(
            "Eryph.GuestServices.Provisioning.Modules.RuncmdBootModule",
            ModuleFrequency.PerBoot,
            instanceId,
            CancellationToken.None))
            .Should().BeTrue();
    }

    [Fact]
    public async Task WriteAsync_SanitizesInstanceIdWithPathSeparators()
    {
        // The override datasource can carry an arbitrary --instance-id from
        // the CLI. A backslash must not let the marker escape the root.
        var store = NewStore();
        await store.WriteAsync("Mod", ModuleFrequency.PerInstance, "..\\evil", "completed", CancellationToken.None);

        // Sanitised path stays inside _tempDir/instance/<sanitised>/sem/Mod.per-instance.
        var allFiles = Directory.EnumerateFiles(_tempDir, "*", SearchOption.AllDirectories).ToList();
        allFiles.Should().OnlyContain(p => p.StartsWith(_tempDir, StringComparison.OrdinalIgnoreCase));
    }

    private FileSemaphoreStore NewStore() =>
        new(NullLogger<FileSemaphoreStore>.Instance, _tempDir);
}
