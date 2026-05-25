using AwesomeAssertions;
using Eryph.GuestServices.Provisioning.Modules;
using Microsoft.Extensions.Logging.Abstractions;

namespace Eryph.GuestServices.Provisioning.Tests.Modules;

public sealed class FileScriptCheckpointStoreTests : IDisposable
{
    private readonly string _root;

    public FileScriptCheckpointStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "egs-scripts-it-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task LoadAsync_returns_Empty_when_no_checkpoint_exists()
    {
        var store = new FileScriptCheckpointStore(NullLogger<FileScriptCheckpointStore>.Instance, _root);
        var checkpoint = await store.LoadAsync("instance-1", CancellationToken.None);
        checkpoint.Executed.Should().BeEmpty();
        checkpoint.RebootCounts.Should().BeEmpty();
    }

    [Fact]
    public async Task SaveAsync_LoadAsync_round_trips_executed_and_reboot_counts()
    {
        var store = new FileScriptCheckpointStore(NullLogger<FileScriptCheckpointStore>.Instance, _root);
        var c = new ScriptCheckpoint
        {
            Executed = [new ScriptCheckpointEntry(1, "abc"), new ScriptCheckpointEntry(2, "def")],
            RebootCounts = new Dictionary<string, int>(StringComparer.Ordinal) { ["2:def"] = 1 },
        };

        await store.SaveAsync("instance-1", c, CancellationToken.None);
        var loaded = await store.LoadAsync("instance-1", CancellationToken.None);

        loaded.Executed.Should().BeEquivalentTo(c.Executed);
        loaded.RebootCounts.Should().ContainKey("2:def").WhoseValue.Should().Be(1);
    }

    [Fact]
    public async Task SaveAsync_writes_atomically_via_tmp_rename()
    {
        // Sanity check that no .tmp lingers after a successful save — a
        // half-written checkpoint would otherwise survive a process crash.
        var store = new FileScriptCheckpointStore(NullLogger<FileScriptCheckpointStore>.Instance, _root);
        await store.SaveAsync("i", new ScriptCheckpoint { Executed = [new(1, "h")] }, CancellationToken.None);

        var instanceDir = Path.Combine(_root, "instance", "i");
        Directory.GetFiles(instanceDir, "*.tmp").Should().BeEmpty();
        File.Exists(Path.Combine(instanceDir, "scripts.json")).Should().BeTrue();
    }

    [Fact]
    public async Task LoadAsync_returns_Empty_when_file_is_corrupt()
    {
        // A torn file (e.g. process killed mid-write to an older format) must
        // not block a fresh run.
        var instanceDir = Path.Combine(_root, "instance", "i");
        Directory.CreateDirectory(instanceDir);
        await File.WriteAllTextAsync(Path.Combine(instanceDir, "scripts.json"), "{ not valid json");

        var store = new FileScriptCheckpointStore(NullLogger<FileScriptCheckpointStore>.Instance, _root);
        var loaded = await store.LoadAsync("i", CancellationToken.None);
        loaded.Executed.Should().BeEmpty();
    }

    [Fact]
    public async Task ResetAsync_removes_the_checkpoint_file()
    {
        var store = new FileScriptCheckpointStore(NullLogger<FileScriptCheckpointStore>.Instance, _root);
        await store.SaveAsync("i", new ScriptCheckpoint { Executed = [new(1, "h")] }, CancellationToken.None);

        await store.ResetAsync("i", CancellationToken.None);

        var loaded = await store.LoadAsync("i", CancellationToken.None);
        loaded.Executed.Should().BeEmpty();
    }

    [Fact]
    public async Task ComputeBodyHash_is_stable_and_changes_with_body()
    {
        var a = ScriptCheckpoint.ComputeBodyHash(System.Text.Encoding.UTF8.GetBytes("hello"));
        var b = ScriptCheckpoint.ComputeBodyHash(System.Text.Encoding.UTF8.GetBytes("hello"));
        var c = ScriptCheckpoint.ComputeBodyHash(System.Text.Encoding.UTF8.GetBytes("hello!"));
        a.Should().Be(b);
        a.Should().NotBe(c);

        // Make sure the function survives non-UTF-8 / binary bodies — the
        // hash is over the raw byte sequence.
        var bin = new byte[] { 0x00, 0xFF, 0x80, 0x7F };
        var d = ScriptCheckpoint.ComputeBodyHash(bin);
        d.Should().NotBeNullOrWhiteSpace();
        await Task.CompletedTask;
    }
}
