using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Management.Infrastructure;

namespace Eryph.GuestServices.Provisioning.Reporting.Handlers;

/// <summary>
/// Supplies the SMBIOS system UUID used as the cloud-init <c>vm_id</c> field in
/// the KVP event key. Eryph's host-side reader ignores it (it reads per-VM); it
/// exists only for byte-fidelity with a generic cloud-init reader.
/// </summary>
public interface IVmIdProvider
{
    string GetVmId();
}

/// <summary>
/// Reads <c>Win32_ComputerSystemProduct.UUID</c> via CIM — the same mechanism
/// cloud-init uses for its vm_id (DMI <c>system-uuid</c>). Returns an empty
/// string on any failure; an absent vm_id leaves the field empty rather than
/// failing the report.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WmiVmIdProvider(ILogger<WmiVmIdProvider> logger) : IVmIdProvider
{
    private const string CimNamespace = @"root\cimv2";

    public string GetVmId()
    {
        try
        {
            using var session = CimSession.Create(null);
            var instance = session
                .EnumerateInstances(CimNamespace, "Win32_ComputerSystemProduct")
                .FirstOrDefault();
            if (instance is null)
                return "";

            using (instance)
                return instance.CimInstanceProperties["UUID"]?.Value as string ?? "";
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to read SMBIOS UUID for the cloud-init vm_id; using an empty value");
            return "";
        }
    }
}
