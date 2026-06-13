using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.Management.Infrastructure;

namespace Eryph.GuestServices.Provisioning.Semaphores;

/// <summary>
/// Reads <c>Win32_OperatingSystem.LastBootUpTime</c> via CIM. Chosen over
/// the undocumented <c>HKLM\...\Session Manager\Memory Management\BootId</c>
/// because LastBootUpTime is a documented Win32 property that is updated
/// on every cold boot AND on resume-from-hibernate. The registry
/// <c>BootId</c> is incremented on cold boot but the semantics around
/// hibernate / fast-startup are not documented.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class Win32BootClock : IBootClock
{
    private const string CimNamespace = @"root\cimv2";

    public string GetCurrentBootId()
    {
        using var session = CimSession.Create(null);
        var instance = session.EnumerateInstances(CimNamespace, "Win32_OperatingSystem").FirstOrDefault()
            ?? throw new InvalidOperationException(
                "No Win32_OperatingSystem instance was returned by CIM; cannot determine boot id.");
        using (instance)
        {
            var value = instance.CimInstanceProperties["LastBootUpTime"]?.Value
                ?? throw new InvalidOperationException(
                    "Win32_OperatingSystem.LastBootUpTime was null; cannot determine boot id.");

            // CIM returns DateTime for CIM_DATETIME properties; format as
            // round-trip ISO so the persisted marker is stable across locales.
            if (value is DateTime dt)
                return dt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

            return value.ToString() ?? throw new InvalidOperationException(
                "Win32_OperatingSystem.LastBootUpTime stringified to null.");
        }
    }

    public DateTimeOffset GetCurrentBootTime()
    {
        using var session = CimSession.Create(null);
        var instance = session.EnumerateInstances(CimNamespace, "Win32_OperatingSystem").FirstOrDefault()
            ?? throw new InvalidOperationException(
                "No Win32_OperatingSystem instance was returned by CIM; cannot determine boot time.");
        using (instance)
        {
            var value = instance.CimInstanceProperties["LastBootUpTime"]?.Value
                ?? throw new InvalidOperationException(
                    "Win32_OperatingSystem.LastBootUpTime was null; cannot determine boot time.");

            if (value is DateTime dt)
                return new DateTimeOffset(dt.ToUniversalTime());

            throw new InvalidOperationException(
                $"Win32_OperatingSystem.LastBootUpTime was not a DateTime (was {value.GetType()}).");
        }
    }
}
