using Eryph.GuestServices.CloudConfig;

namespace Eryph.GuestServices.Provisioning.Update;

/// <summary>
/// Prepares a self-update: resolves the target from the release index,
/// downloads and verifies the Windows package, and stages it ready for the
/// out-of-process updater to swap in. All network/disk work lives here so the
/// <c>EgsModule</c> only sees a yes/no plan.
/// </summary>
public interface IEgsUpdater
{
    /// <summary>
    /// Returns a staged <see cref="UpdatePlan"/> when an update is due and the
    /// package was downloaded + verified, or <c>null</c> when no update is
    /// needed or the attempt could not be prepared (logged; never throws for a
    /// transient fetch failure — a broken update must not fail provisioning).
    /// </summary>
    Task<UpdatePlan?> PrepareAsync(EgsUpdateConfig? config, CancellationToken cancellationToken);
}

/// <summary>A staged, verified update ready to be applied by the updater process.</summary>
public sealed record UpdatePlan
{
    /// <summary>Directory the verified new binaries were extracted into.</summary>
    public required string StagingDirectory { get; init; }

    /// <summary>The version that will be running after the swap.</summary>
    public required string TargetVersion { get; init; }
}
