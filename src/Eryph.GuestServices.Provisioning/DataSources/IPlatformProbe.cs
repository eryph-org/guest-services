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
}
