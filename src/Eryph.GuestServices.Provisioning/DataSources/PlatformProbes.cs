using System.Runtime.InteropServices;
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

    public bool IsRunningOnOpenStack() => PlatformProbes.IsRunningOnOpenStack();
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

    // cloud-init DataSourceOpenStack.ds_detect: DMI system-product-name values
    // (helpers/openstack.py / sources/DataSourceOpenStack.py).
    internal static readonly string[] OpenStackProductNames =
    [
        "OpenStack Nova",
        "OpenStack Compute",
    ];

    // chassis-asset-tag values: the product names plus the OpenStack-derived
    // public clouds cloud-init recognises (HUAWEICLOUD, OpenTelekomCloud, …).
    internal static readonly string[] OpenStackAssetTags =
    [
        "OpenStack Nova",
        "OpenStack Compute",
        "HUAWEICLOUD",
        "OpenTelekomCloud",
        "Samsung Cloud Platform",
        "SAP CCloud VM",
    ];

    public static bool IsRunningOnAzure() =>
        ReadAzureVmId() is not null
        || string.Equals(ReadChassisAssetTag(), AzureChassisAssetTag, StringComparison.Ordinal);

    public static bool IsRunningOnOpenStack()
    {
        // cloud-init parity (DataSourceOpenStack.ds_detect): non-x86 CPUs don't
        // reliably report DMI product names, so OpenStack is accepted on any
        // non-x86 architecture unconditionally — the liveness probe is then the
        // real arbiter. Omitting this would make us stricter than cloud-init on
        // ARM64 guests. (We deliberately do NOT mirror cloud-init's Oracle path —
        // no Oracle datasource here — nor its Linux /proc/1/environ check.)
        if (!IsX86())
            return true;

        var productName = ReadSystemProductName()?.Trim();
        if (!string.IsNullOrEmpty(productName)
            && OpenStackProductNames.Any(n => string.Equals(n, productName, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var assetTag = ReadChassisAssetTag()?.Trim();
        return !string.IsNullOrEmpty(assetTag)
            && OpenStackAssetTags.Any(t => string.Equals(t, assetTag, StringComparison.OrdinalIgnoreCase));
    }

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

    // cloud-init util.is_x86(): machine in {i?86, x86_64}. Anything else (ARM,
    // etc.) is treated as non-x86 for the OpenStack DMI gate.
    private static bool IsX86() =>
        RuntimeInformation.OSArchitecture is Architecture.X86 or Architecture.X64;

    private static string? ReadSystemProductName()
    {
        if (!OperatingSystem.IsWindows())
            return null;
        try
        {
            return ReadSystemProductNameCore();
        }
        catch
        {
            return null;
        }
    }

    // SMBIOS Type-1 "Product Name" — the same value cloud-init reads as DMI
    // system-product-name (/sys/class/dmi/id/product_name).
    [SupportedOSPlatform("windows")]
    private static string? ReadSystemProductNameCore()
    {
        using var session = CimSession.Create(null);
        foreach (var instance in session.EnumerateInstances(@"root\cimv2", "Win32_ComputerSystemProduct"))
        {
            using (instance)
            {
                var value = instance.CimInstanceProperties["Name"]?.Value;
                if (value is string s && !string.IsNullOrWhiteSpace(s))
                    return s.Trim();
            }
        }

        return null;
    }
}
