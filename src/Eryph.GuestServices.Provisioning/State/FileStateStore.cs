using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Eryph.GuestServices.Provisioning.State;

public sealed class FileStateStore(ILogger<FileStateStore> logger) : IStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

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
            return await JsonSerializer.DeserializeAsync<ProvisioningState>(stream, JsonOptions, cancellationToken)
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
            await JsonSerializer.SerializeAsync(stream, state, JsonOptions, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        File.Move(tempPath, _statePath, overwrite: true);
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
