using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Eryph.GuestServices.Provisioning.DataSources;

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
    public static bool IsRunningOnAzure() => ReadAzureVmId() is not null;

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
}
