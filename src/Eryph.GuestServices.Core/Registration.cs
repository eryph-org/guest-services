using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Eryph.GuestServices.Core;

[SupportedOSPlatform("windows")]
public static class Registration
{
    public static void Register(Guid id, string name)
    {
        var servicesKey = Registry.LocalMachine.OpenSubKey(
                              @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Virtualization\GuestCommunicationServices",
                              writable: true)
                          ?? throw new InvalidOperationException("Could not open the GuestCommunicationServices registry.");

        var serviceKey = servicesKey.CreateSubKey(id.ToString().ToUpperInvariant())
                         ?? throw new InvalidOperationException("Could not create registry key for eryph guest service.");

        serviceKey.SetValue("ElementName", name);
    }

    public static void Unregister(Guid id)
    {
        var servicesKey = Registry.LocalMachine.OpenSubKey(
                              @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Virtualization\GuestCommunicationServices",
                              writable: true)
                          ?? throw new InvalidOperationException("Could not open the GuestCommunicationServices registry.");
        servicesKey.DeleteSubKeyTree(id.ToString().ToUpperInvariant(), throwOnMissingSubKey: false);
    }
}
