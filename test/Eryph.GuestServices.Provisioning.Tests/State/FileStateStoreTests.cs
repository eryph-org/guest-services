using AwesomeAssertions;
using Eryph.GuestServices.Provisioning.State;
using Microsoft.Extensions.Logging.Abstractions;

namespace Eryph.GuestServices.Provisioning.Tests.State;

public sealed class FileStateStoreTests : IDisposable
{
    private readonly string _tempDir;

    public FileStateStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "egs-prov-state-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task LoadAsync_returns_null_when_file_missing()
    {
        var store = new FileStateStore(NullLogger<FileStateStore>.Instance, _tempDir);

        var result = await store.LoadAsync(CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task SaveAsync_then_LoadAsync_roundtrips()
    {
        var store = new FileStateStore(NullLogger<FileStateStore>.Instance, _tempDir);
        var now = DateTimeOffset.UtcNow;
        var state = new ProvisioningState
        {
            InstanceId = "i-abc",
            CompletedStages = ["Discovery", "Hostname"],
            CompletedHandlers = ["NS.HostnameHandler"],
            RebootCount = 1,
            StartedAt = now,
            LastUpdated = now,
        };

        await store.SaveAsync(state, CancellationToken.None);
        var loaded = await store.LoadAsync(CancellationToken.None);

        loaded.Should().NotBeNull();
        loaded!.InstanceId.Should().Be("i-abc");
        loaded.CompletedStages.Should().BeEquivalentTo(["Discovery", "Hostname"]);
        loaded.CompletedHandlers.Should().BeEquivalentTo(["NS.HostnameHandler"]);
        loaded.RebootCount.Should().Be(1);
    }

    [Fact]
    public async Task SaveAsync_overwrites_existing_file_atomically()
    {
        var store = new FileStateStore(NullLogger<FileStateStore>.Instance, _tempDir);

        await store.SaveAsync(new ProvisioningState { InstanceId = "v1" }, CancellationToken.None);
        await store.SaveAsync(new ProvisioningState { InstanceId = "v2" }, CancellationToken.None);

        var loaded = await store.LoadAsync(CancellationToken.None);
        loaded!.InstanceId.Should().Be("v2");
        Directory.GetFiles(_tempDir).Should().ContainSingle(p => Path.GetFileName(p) == "state.json");
    }

    [Fact]
    public async Task ResetAsync_deletes_state_file()
    {
        var store = new FileStateStore(NullLogger<FileStateStore>.Instance, _tempDir);
        await store.SaveAsync(new ProvisioningState { InstanceId = "x" }, CancellationToken.None);

        await store.ResetAsync(CancellationToken.None);

        (await store.LoadAsync(CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task LoadAsync_returns_null_on_corrupt_file()
    {
        var path = Path.Combine(_tempDir, "state.json");
        await File.WriteAllTextAsync(path, "{ not json");

        var store = new FileStateStore(NullLogger<FileStateStore>.Instance, _tempDir);
        var result = await store.LoadAsync(CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task Concurrent_LoadAsync_calls_succeed()
    {
        var store = new FileStateStore(NullLogger<FileStateStore>.Instance, _tempDir);
        await store.SaveAsync(new ProvisioningState { InstanceId = "i-1" }, CancellationToken.None);

        var tasks = Enumerable.Range(0, 8).Select(_ => store.LoadAsync(CancellationToken.None)).ToArray();
        var results = await Task.WhenAll(tasks);

        results.Should().AllSatisfy(s => s!.InstanceId.Should().Be("i-1"));
    }
}
