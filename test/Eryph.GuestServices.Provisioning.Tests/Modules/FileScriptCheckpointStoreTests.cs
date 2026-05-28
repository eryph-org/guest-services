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
        checkpoint.Completed.Should().BeEmpty();
        checkpoint.Progress.Should().BeEmpty();
    }

    [Fact]
    public async Task SaveAsync_LoadAsync_round_trips_completed_and_progress()
    {
        var store = new FileScriptCheckpointStore(NullLogger<FileScriptCheckpointStore>.Instance, _root);
        var c = new UserCodeCheckpoint
        {
            Completed = [new CheckpointEntry(1, "abc"), new CheckpointEntry(2, "def")],
            Progress = new Dictionary<string, EntryProgress>(StringComparer.Ordinal)
            {
                ["3:ghi"] = new() { RebootAttempts = 2, OverrideLimit = 5 },
            },
        };

        await store.SaveAsync("instance-1", c, CancellationToken.None);
        var loaded = await store.LoadAsync("instance-1", CancellationToken.None);

        loaded.Completed.Should().BeEquivalentTo(c.Completed);
        loaded.Progress.Should().ContainKey("3:ghi");
        loaded.Progress["3:ghi"].RebootAttempts.Should().Be(2);
        loaded.Progress["3:ghi"].OverrideLimit.Should().Be(5);
    }

    [Fact]
    public async Task SaveAsync_writes_atomically_via_tmp_rename()
    {
        // Sanity check that no .tmp lingers after a successful save — a
        // half-written checkpoint would otherwise survive a process crash.
        var store = new FileScriptCheckpointStore(NullLogger<FileScriptCheckpointStore>.Instance, _root);
        await store.SaveAsync("i", new UserCodeCheckpoint { Completed = [new(1, "h")] }, CancellationToken.None);

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
        loaded.Completed.Should().BeEmpty();
    }

    [Fact]
    public async Task ResetAsync_removes_the_checkpoint_file()
    {
        var store = new FileScriptCheckpointStore(NullLogger<FileScriptCheckpointStore>.Instance, _root);
        await store.SaveAsync("i", new UserCodeCheckpoint { Completed = [new(1, "h")] }, CancellationToken.None);

        await store.ResetAsync("i", CancellationToken.None);

        var loaded = await store.LoadAsync("i", CancellationToken.None);
        loaded.Completed.Should().BeEmpty();
    }
}
