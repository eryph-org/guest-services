using System.Runtime.Versioning;
using Microsoft.Management.Infrastructure;
using Microsoft.Win32;

namespace Eryph.GuestServices.Provisioning.DataSources;

/// <summary>
/// Default <see cref="IPlatformProbe"/> implementation. Production callers go
/// through the injected interface so an Azure-hosted CI agent's ambient signals
/// can't flip the datasource probes; this class holds the real registry/chassis
/// detection.
/// </summary>
public sealed class PlatformProbe : IPlatformProbe
{
    public bool IsRunningOnAzure() => PlatformProbes.IsRunningOnAzure();
}

/// <summary>
/// Cheap platform indicators used by lower-priority datasources to decline
/// when a platform-native datasource owns the chain. This is belt-and-suspenders
/// on top of <see cref="IDataSource.Priority"/> ordering: even if Azure's
/// datasource hasn't been fully implemented yet, NoCloud / ConfigDrive defer
/// when an Azure context is detected so we don't accidentally claim a disk
/// that belongs to Azure's Provisioning Agent.
/// </summary>
internal static class PlatformProbes
{
    // Well-known SMBIOS chassis asset tag burned into every Azure VM. Mirrors
    // cloudbase-init's _check_for_asset_tag() (same constant, same intent).
    internal const string AzureChassisAssetTag = "7783-7084-3265-9085-8269-3286-77";

    public static bool IsRunningOnAzure() =>
        ReadAzureVmId() is not null
        || string.Equals(ReadChassisAssetTag(), AzureChassisAssetTag, StringComparison.Ordinal);

    private static string? ReadAzureVmId()
    {
        if (!OperatingSystem.IsWindows())
            return null;
        try
        {
            return ReadAzureVmIdCore();
        }
        catch
        {
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static string? ReadAzureVmIdCore()
    {
        using var key = Registry.LocalMachine.OpenSubKey(AzureDataSource.AzureVmIdKey);
        return key?.GetValue(AzureDataSource.AzureVmIdValue) as string;
    }

    private static string? ReadChassisAssetTag()
    {
        if (!OperatingSystem.IsWindows())
            return null;
        try
        {
            return ReadChassisAssetTagCore();
        }
        catch
        {
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static string? ReadChassisAssetTagCore()
    {
        using var session = CimSession.Create(null);
        foreach (var instance in session.EnumerateInstances(@"root\cimv2", "Win32_SystemEnclosure"))
        {
            using (instance)
            {
                var value = instance.CimInstanceProperties["SMBIOSAssetTag"]?.Value;
                if (value is string s && !string.IsNullOrWhiteSpace(s))
                    return s.Trim();
            }
        }

        return null;
    }
}
