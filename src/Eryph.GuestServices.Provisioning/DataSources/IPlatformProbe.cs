namespace Eryph.GuestServices.Provisioning.DataSources;

/// <summary>
/// Cheap platform indicators used by lower-priority datasources to decline when
/// a platform-native datasource owns the chain. Injected (rather than read
/// statically) so the ambient host platform — notably an Azure-hosted CI build
/// agent — cannot flip the datasource probes under test.
/// </summary>
public interface IPlatformProbe
{
    bool IsRunningOnAzure();

    /// <summary>
    /// True when the platform looks like OpenStack — the metadata-service
    /// datasource's <c>ds_detect</c> gate. Mirrors cloud-init's
    /// <c>DataSourceOpenStack.ds_detect</c>: any non-x86 architecture (DMI is
    /// unreliable there), or DMI <c>system-product-name</c> / <c>chassis-asset-tag</c>
    /// matching the known OpenStack / OpenStack-derived cloud signatures.
    /// </summary>
    bool IsRunningOnOpenStack();
}
