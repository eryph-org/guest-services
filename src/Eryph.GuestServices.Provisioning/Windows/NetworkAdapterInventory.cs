using System.Net.NetworkInformation;
using System.Runtime.Versioning;

namespace Eryph.GuestServices.Provisioning.Windows;

/// <summary>
/// Enumerates the guest's network adapters and their MAC addresses with the
/// built-in <see cref="NetworkInterface"/> APIs. The MAC comes from
/// <see cref="NetworkInterface.GetPhysicalAddress"/> (the hardware address),
/// which is reliable across NIC types and platforms — unlike the
/// <c>MSFT_NetAdapter.MacAddress</c> CIM property, which is empty unless the OS
/// overrides the MAC (so a Hyper-V vNIC reports it blank and the address only
/// lives in <c>PermanentAddress</c>). CIM is still used to <em>set</em>
/// addresses/DNS/routes, keyed by the interface index resolved here.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class NetworkAdapterInventory
{
    public static IReadOnlyList<NetworkAdapterInfo> Enumerate()
    {
        var results = new List<NetworkAdapterInfo>();
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            // One transitional/odd adapter must not abort the whole enumeration
            // (GetIPProperties and the family lookups can throw); skip it.
            try
            {
                var info = TryDescribe(adapter);
                if (info is not null)
                    results.Add(info);
            }
            catch (NetworkInformationException)
            {
                // Adapter in a transitional state (being removed / not yet
                // registered) — skip it rather than fail the whole inventory.
            }
        }

        return results;
    }

    private static NetworkAdapterInfo? TryDescribe(NetworkInterface adapter)
    {
        // Only adapters that can carry a network-config MAC. Loopback and
        // tunnel/teredo interfaces never match a delivered Ethernet entry.
        if (adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback
            or NetworkInterfaceType.Tunnel)
            return null;

        var macBytes = adapter.GetPhysicalAddress().GetAddressBytes();
        // Require a real 6-byte, non-zero MAC. An all-zero address (some
        // not-yet-up virtual adapters report it) must not become a match key.
        if (macBytes.Length != 6 || macBytes.All(b => b == 0))
            return null;

        var index = TryGetInterfaceIndex(adapter);
        if (index == 0)
            return null;

        return new NetworkAdapterInfo
        {
            InterfaceAlias = adapter.Name,
            InterfaceIndex = index,
            MacAddress = FormatMac(macBytes),
        };
    }

    // The Windows interface index (IfIndex) the CIM set-methods key on. It is
    // exposed via the IP-family properties; for a dual-stack NIC the IPv4 and
    // IPv6 indices are the same adapter IfIndex, and the verified path uses the
    // IPv4 index. A freshly-booted NIC has at least an IPv6 link-local, so the
    // fallback still yields the index for an otherwise IPv4-less adapter.
    private static int TryGetInterfaceIndex(NetworkInterface adapter)
    {
        var properties = adapter.GetIPProperties();
        try
        {
            return properties.GetIPv4Properties().Index;
        }
        catch (NetworkInformationException)
        {
            // IPv4 not configured on this adapter yet — try IPv6 below.
        }

        try
        {
            return properties.GetIPv6Properties().Index;
        }
        catch (NetworkInformationException)
        {
            // Neither family is configured — no index to drive CIM with.
        }

        return 0;
    }

    // Lowercase, colon-separated — the cloud-init form the matcher normalises to.
    internal static string FormatMac(byte[] mac) =>
        string.Join(':', mac.Select(b => b.ToString("x2")));
}
