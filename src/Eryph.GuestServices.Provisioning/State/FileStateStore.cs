using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Eryph.GuestServices.Provisioning.State;

public sealed class FileStateStore(ILogger<FileStateStore> logger) : IStateStore
{

    private readonly string _stateDirectory = DefaultStateDirectory();
    private readonly string _statePath = Path.Combine(DefaultStateDirectory(), "state.json");

    // Override for tests.
    public FileStateStore(ILogger<FileStateStore> logger, string stateDirectory)
        : this(logger)
    {
        _stateDirectory = stateDirectory;
        _statePath = Path.Combine(stateDirectory, "state.json");
    }

    public async Task<ProvisioningState?> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_statePath))
            return null;

        try
        {
            await using var stream = File.Open(_statePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return await JsonSerializer.DeserializeAsync(stream, StateStoreJsonContext.Default.ProvisioningState, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "State file at {Path} is corrupt; treating as missing", _statePath);
            return null;
        }
    }

    public async Task SaveAsync(ProvisioningState state, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_stateDirectory);
        var tempPath = _statePath + ".tmp";

        await using (var stream = File.Open(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(stream, state, StateStoreJsonContext.Default.ProvisioningState, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        await AtomicFile.ReplaceWithRetryAsync(tempPath, _statePath, logger, cancellationToken).ConfigureAwait(false);
    }

    public Task ResetAsync(CancellationToken cancellationToken)
    {
        if (File.Exists(_statePath))
            File.Delete(_statePath);
        return Task.CompletedTask;
    }

    private static string DefaultStateDirectory()
    {
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return Path.Combine(programData, "eryph", "provisioning");
    }
}

[JsonSerializable(typeof(ProvisioningState))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class StateStoreJsonContext : JsonSerializerContext;
