using Eryph.GuestServices.Provisioning.DataSources.OpenStack;
using Microsoft.Extensions.Logging;

namespace Eryph.GuestServices.Provisioning.DataSources;

/// <summary>
/// OpenStack metadata-service datasource — the HTTP variant of cloud-init's
/// <c>DataSourceOpenStack</c> (the <c>169.254.169.254/openstack/&lt;version&gt;/…</c>
/// endpoint), as opposed to the disk-based <see cref="ConfigDriveDataSource"/>.
/// Both share <see cref="OpenStackMetadataReader"/>; this one fetches over HTTP.
///
/// Discovery (spec §8): a DMI gate (<c>system-product-name</c> /
/// <c>chassis-asset-tag</c> via <see cref="IPlatformProbe.IsRunningOnOpenStack"/>)
/// followed by a liveness probe (<c>GET /openstack</c>). The DMI gate keeps us
/// from hammering the link-local address on non-OpenStack guests; the liveness
/// probe lets us wait for the network to come up.
///
/// MATURITY: "technically working", NOT production-ready. Validated against the
/// captured nova fixture (unit tests, both transports) and end-to-end against the
/// <c>egs-openstack-sim</c> simulator on eryph/Hyper-V — but never against a real
/// OpenStack deployment, where link-local reachability and dynamic IMDS behavior
/// differ. See DESIGN.md ("Maturity") before relying on this in production.
/// </summary>
public sealed class OpenStackMetadataDataSource : IDataSource
{
    private readonly ILogger<OpenStackMetadataDataSource> _logger;
    private readonly Func<OpenStackMetadataClient> _clientFactory;
    private readonly Func<bool> _isRunningOnOpenStack;
    private readonly Func<bool> _isRunningOnAzure;

    /// <summary>Production constructor. DMI detection flows through the injected
    /// <see cref="IPlatformProbe"/> so a CI host's ambient platform can't flip
    /// the gate under test.</summary>
    public OpenStackMetadataDataSource(IPlatformProbe platformProbe, ILogger<OpenStackMetadataDataSource> logger)
        : this(
            logger,
            clientFactory: () => new OpenStackMetadataClient(logger),
            isRunningOnOpenStack: platformProbe.IsRunningOnOpenStack,
            isRunningOnAzure: platformProbe.IsRunningOnAzure)
    {
    }

    /// <summary>Test seam: injectable client factory + platform gates so the
    /// read/ready path can be exercised on a non-OpenStack host.</summary>
    internal OpenStackMetadataDataSource(
        ILogger<OpenStackMetadataDataSource> logger,
        Func<OpenStackMetadataClient> clientFactory,
        Func<bool> isRunningOnOpenStack,
        Func<bool> isRunningOnAzure)
    {
        _logger = logger;
        _clientFactory = clientFactory;
        _isRunningOnOpenStack = isRunningOnOpenStack;
        _isRunningOnAzure = isRunningOnAzure;
    }

    public string Name => "OpenStack";

    // After the disk-based ConfigDrive (40): a config-2 ISO needs no network and
    // is the cheaper, more reliable source when both are present.
    public int Priority => 50;

    public bool RequiresNetwork => true;

    public async Task<DataSourceProbeResult> ProbeAsync(CancellationToken cancellationToken)
    {
        // Defensive opt-out: Azure owns its own chain (higher priority); never
        // claim the link-local address there.
        if (_isRunningOnAzure())
            return DataSourceProbeResult.NotApplicable.Instance;

        // DMI gate: cloud-init ds_detect. Skip the HTTP probe entirely on guests
        // that don't advertise an OpenStack SMBIOS signature.
        if (!_isRunningOnOpenStack())
            return DataSourceProbeResult.NotApplicable.Instance;

        try
        {
            using var client = _clientFactory();

            // Liveness: GET /openstack. If the service isn't answering yet the
            // link-local network may still be coming up — back off and retry
            // rather than declaring the datasource absent.
            if (!await client.ProbeLivenessAsync(cancellationToken).ConfigureAwait(false))
            {
                return new DataSourceProbeResult.WaitForReady(
                    "OpenStack metadata service not reachable yet",
                    TimeSpan.FromSeconds(5));
            }

            var data = await OpenStackMetadataReader
                .ReadAsync(
                    new HttpMetadataTransport(client),
                    sourceName: "OpenStack",
                    subplatform: "metadata-service",
                    cancellationToken)
                .ConfigureAwait(false);

            if (data is null)
            {
                // Liveness passed but no selectable meta_data.json — the service
                // answered /openstack but isn't serving v2 metadata for us.
                _logger.LogInformation(
                    "OpenStack metadata service reachable but no usable meta_data.json found");
                return DataSourceProbeResult.NotApplicable.Instance;
            }

            _logger.LogInformation(
                "OpenStack metadata-service datasource located instance {InstanceId}",
                data.InstanceId);
            return new DataSourceProbeResult.Ready(data);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new DataSourceProbeResult.Failed(
                $"OpenStack metadata service datasource failed: {ex.Message}", ex);
        }
    }

    public Task OnCompletedAsync(DataSourceResult data, CancellationToken cancellationToken)
    {
        // Nothing to eject/clean up for an HTTP source. The host owns the
        // metadata service lifetime.
        return Task.CompletedTask;
    }
}
