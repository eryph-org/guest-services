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
        // There is exactly one Win32_ComputerSystem instance per machine.
        var instance = session.EnumerateInstances(CimNamespace, "Win32_ComputerSystem").First();
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
