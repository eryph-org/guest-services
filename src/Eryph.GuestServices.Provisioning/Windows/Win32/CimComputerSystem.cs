using System.Runtime.Versioning;
using Microsoft.Management.Infrastructure;

namespace Eryph.GuestServices.Provisioning.Windows.Win32;

/// <summary>
/// Thin wrapper around <c>Win32_ComputerSystem.Rename</c> via CIM. Lives in
/// its own file so the orchestrator in <see cref="WindowsOs"/> stays clean.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class CimComputerSystem
{
    private const string CimNamespace = @"root\cimv2";

    public static uint Rename(string newName)
    {
        using var session = CimSession.Create(null);
        // There is exactly one Win32_ComputerSystem instance per machine on a
        // healthy install, but defensive code beats an opaque
        // InvalidOperationException from LINQ if the enumeration is empty.
        var instance = session.EnumerateInstances(CimNamespace, "Win32_ComputerSystem").FirstOrDefault()
            ?? throw new InvalidOperationException(
                "No Win32_ComputerSystem instance was returned by CIM; cannot rename this computer.");
        using (instance)
        {
            using var parameters = new CimMethodParametersCollection
            {
                CimMethodParameter.Create("Name", newName, CimType.String, CimFlags.None),
            };

            using var result = session.InvokeMethod(instance, "Rename", parameters);
            return (uint)(result.ReturnValue.Value ?? 0u);
        }
    }
}
