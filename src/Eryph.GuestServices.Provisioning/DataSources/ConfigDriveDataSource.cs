using Eryph.GuestServices.Provisioning.DataSources.OpenStack;
using Microsoft.Extensions.Logging;

namespace Eryph.GuestServices.Provisioning.DataSources;

public sealed class ConfigDriveDataSource(
    IVolumeProbe volumeProbe,
    IPlatformProbe platformProbe,
    ILogger<ConfigDriveDataSource> logger) : IDataSource
{
    private const string ExpectedLabel = "config-2";

    public string Name => "ConfigDrive";

    public int Priority => 40;

    public bool RequiresNetwork => false;

    public async Task<DataSourceProbeResult> ProbeAsync(CancellationToken cancellationToken)
    {
        var volume = volumeProbe.EnumerateVolumes()
            .FirstOrDefault(v => string.Equals(v.Label, ExpectedLabel, StringComparison.OrdinalIgnoreCase));

        if (volume is null)
            return DataSourceProbeResult.NotApplicable.Instance;

        // Defensive opt-out: if we're on Azure, the config-2 disk (the label
        // Azure uses for its PA delivery in some scenarios) belongs to PA, not
        // to OpenStack. AzureDataSource has higher priority but it's a stub today.
        if (platformProbe.IsRunningOnAzure())
        {
            logger.LogInformation(
                "ConfigDrive volume at {Root} found but Azure context detected; declining to claim it",
                volume.RootPath);
            return DataSourceProbeResult.NotApplicable.Instance;
        }

        logger.LogInformation("ConfigDrive datasource located volume at {Root}", volume.RootPath);

        try
        {
            var data = await ReadAsync(volume.RootPath, cancellationToken).ConfigureAwait(false);
            if (data is null)
                return DataSourceProbeResult.NotApplicable.Instance;

            return new DataSourceProbeResult.Ready(data);
        }
        catch (Exception ex)
        {
            return new DataSourceProbeResult.Failed($"ConfigDrive datasource is malformed: {ex.Message}", ex);
        }
    }

    public Task OnCompletedAsync(DataSourceResult data, CancellationToken cancellationToken)
    {
        // RFC 0005: same rationale as NoCloud — eryph-zero keeps the config-2
        // ISO attached so a `egs-tool reset` can re-read the same payload.
        // cloud-init's OpenStack ConfigDrive datasource doesn't eject the
        // volume on success either; the host owns its lifetime.
        return Task.CompletedTask;
    }

    /// <summary>
    /// Reads the OpenStack v2 metadata tree from a mounted config-2 volume at
    /// <paramref name="root"/>. Shared with the metadata-service datasource via
    /// <see cref="OpenStackMetadataReader"/>; the only difference is the
    /// file-vs-HTTP transport.
    /// </summary>
    internal static Task<DataSourceResult?> ReadAsync(string root, CancellationToken cancellationToken) =>
        OpenStackMetadataReader.ReadAsync(
            new FileMetadataTransport(root),
            sourceName: "ConfigDrive",
            subplatform: "config-drive",
            cancellationToken);
}
