using System.Net.NetworkInformation;

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
internal static class NetworkAdapterInventory
{
    public static IReadOnlyList<NetworkAdapterInfo> Enumerate()
    {
        var results = new List<NetworkAdapterInfo>();
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            // Only adapters that can carry a network-config MAC. Loopback and
            // tunnel/teredo interfaces never match a delivered Ethernet entry.
            if (adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback
                or NetworkInterfaceType.Tunnel)
                continue;

            var macBytes = adapter.GetPhysicalAddress().GetAddressBytes();
            if (macBytes.Length != 6)
                continue;

            var index = TryGetInterfaceIndex(adapter);
            if (index == 0)
                continue;

            results.Add(new NetworkAdapterInfo
            {
                InterfaceAlias = adapter.Name,
                InterfaceIndex = index,
                MacAddress = FormatMac(macBytes),
                // Everything that survived the filter above is a real,
                // MAC-bearing NIC the config can target. Matching is by exact
                // MAC, so any extra virtual adapter simply never matches.
                IsPhysical = true,
            });
        }

        return results;
    }

    // The Windows interface index (IfIndex) the CIM set-methods key on. It is
    // exposed via the IP-family properties; a freshly-booted NIC has at least an
    // IPv6 link-local, so the IPv4 -> IPv6 fallback covers DHCP and static.
    private static int TryGetInterfaceIndex(NetworkInterface adapter)
    {
        var properties = adapter.GetIPProperties();
        try
        {
            return properties.GetIPv4Properties().Index;
        }
        catch (NetworkInformationException)
        {
        }

        try
        {
            return properties.GetIPv6Properties().Index;
        }
        catch (NetworkInformationException)
        {
        }

        return 0;
    }

    // Lowercase, colon-separated — the cloud-init form the matcher normalises to.
    internal static string FormatMac(byte[] mac) =>
        string.Join(':', mac.Select(b => b.ToString("x2")));
}
