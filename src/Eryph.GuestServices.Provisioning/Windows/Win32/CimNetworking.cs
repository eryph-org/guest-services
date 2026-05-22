using System.Globalization;
using System.Net;
using System.Runtime.Versioning;
using Microsoft.Management.Infrastructure;

namespace Eryph.GuestServices.Provisioning.Windows.Win32;

/// <summary>
/// Thin wrappers around the NetTCPIP / NetAdapter CIM classes under
/// <c>root\StandardCimv2</c>. These are the same classes that back the
/// PowerShell NetTCPIP module (Get-NetAdapter, Set-NetIPInterface, ...) but
/// without the powershell.exe startup cost.
/// </summary>
/// <remarks>
/// We deliberately keep all CIM interaction in one place so the platform
/// surface in <see cref="WindowsOs"/> stays readable. Every public method
/// here is idempotent: it inspects current state, computes the delta, and
/// applies only the required changes. The CIM cmdlets themselves raise on
/// "no-op" calls (e.g. removing a non-existent address), so the delta logic
/// is what makes re-runs safe.
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class CimNetworking
{
    private const string StandardCimNamespace = @"root\StandardCimv2";

    public static IReadOnlyList<NetworkAdapterInfo> EnumerateAdapters()
    {
        using var session = CimSession.Create(null);

        var results = new List<NetworkAdapterInfo>();
        // MSFT_NetAdapter surfaces both physical and virtual NICs; the
        // ConnectorPresent boolean is the most reliable "this is a hardware
        // NIC" indicator (it's set for real NDIS adapters and false for
        // loopback / tunnel / virtual switch endpoints).
        foreach (var instance in session.EnumerateInstances(StandardCimNamespace, "MSFT_NetAdapter"))
        {
            using (instance)
            {
                var ifIndex = GetUInt32(instance, "InterfaceIndex");
                if (ifIndex == 0)
                    continue;

                var alias = GetString(instance, "Name")
                    ?? GetString(instance, "InterfaceAlias")
                    ?? $"Interface {ifIndex}";

                // MacAddress as MSFT_NetAdapter reports it is uppercase with
                // hyphens ("AA-BB-CC-DD-EE-FF"). Cloud-init style is lowercase
                // colon-separated; normalise so callers can compare directly.
                var mac = NormaliseMac(GetString(instance, "MacAddress"));

                // Physical NICs report ConnectorPresent=true. Falling back to
                // the "Virtual" flag (when present) handles older hosts that
                // don't expose ConnectorPresent uniformly.
                var connectorPresent = GetBoolean(instance, "ConnectorPresent");
                var isVirtual = GetBoolean(instance, "Virtual");
                var isPhysical = (connectorPresent ?? false) && !(isVirtual ?? false);

                results.Add(new NetworkAdapterInfo
                {
                    InterfaceAlias = alias,
                    InterfaceIndex = (int)ifIndex,
                    MacAddress = mac,
                    IsPhysical = isPhysical,
                });
            }
        }

        return results;
    }

    public static void SetDhcp(int interfaceIndex, bool enabled)
    {
        using var session = CimSession.Create(null);

        var iface = FindIpInterface(session, interfaceIndex, addressFamily: 2 /* IPv4 */);
        if (iface is null)
            return;

        using (iface)
        {
            var current = (byte?)iface.CimInstanceProperties["Dhcp"]?.Value;
            var desired = (byte)(enabled ? 1 : 0); // 1=Enabled, 0=Disabled
            if (current == desired)
                return;

            iface.CimInstanceProperties["Dhcp"].Value = desired;
            session.ModifyInstance(StandardCimNamespace, iface);
        }

        if (!enabled)
        {
            // Clear DHCP-leased addresses so the manual set we'll apply next
            // is the sole source of addresses on the interface.
            RemoveDhcpAddresses(session, interfaceIndex);
        }
    }

    public static void SetStaticIpv4Addresses(int interfaceIndex, IReadOnlyList<string> desired)
    {
        using var session = CimSession.Create(null);

        var existing = ListIpAddresses(session, interfaceIndex, addressFamily: 2)
            .ToDictionary(a => $"{a.Address}/{a.PrefixLength}", StringComparer.OrdinalIgnoreCase);

        var desiredSet = desired.Select(NormaliseCidr).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Remove addresses that are no longer wanted.
        foreach (var (key, addr) in existing)
        {
            if (!desiredSet.Contains(key))
                session.DeleteInstance(StandardCimNamespace, addr.Instance);
        }

        // Add new addresses. The CIM provider rejects duplicates, so we skip
        // anything already present.
        foreach (var cidr in desiredSet)
        {
            if (existing.ContainsKey(cidr))
                continue;

            var (address, prefix) = ParseCidr(cidr);
            using var parameters = new CimMethodParametersCollection
            {
                CimMethodParameter.Create("InterfaceIndex", (uint)interfaceIndex, CimType.UInt32, CimFlags.None),
                CimMethodParameter.Create("IPAddress", address, CimType.String, CimFlags.None),
                CimMethodParameter.Create("PrefixLength", (byte)prefix, CimType.UInt8, CimFlags.None),
                CimMethodParameter.Create("AddressFamily", (ushort)2 /* IPv4 */, CimType.UInt16, CimFlags.None),
            };
            using var result = session.InvokeMethod(StandardCimNamespace, "MSFT_NetIPAddress", "Create", parameters);
            CheckReturn(result, $"MSFT_NetIPAddress.Create({address}/{prefix})");
        }

        // Dispose the instance handles we held open.
        foreach (var (_, addr) in existing)
            addr.Instance.Dispose();
    }

    public static void SetIpv4DefaultGateway(int interfaceIndex, string? gateway)
    {
        using var session = CimSession.Create(null);

        // Existing default routes on this interface — we remove any that
        // don't match the desired gateway so the result is a single 0/0
        // route to the wanted next-hop (or none if gateway is null).
        var existing = ListDefaultRoutes(session, interfaceIndex, addressFamily: 2).ToList();

        CimInstance? keepInstance = null;
        if (gateway is not null)
        {
            foreach (var r in existing)
            {
                if (string.Equals(r.NextHop, gateway, StringComparison.OrdinalIgnoreCase))
                {
                    keepInstance = r.Instance;
                    break;
                }
            }
        }

        foreach (var route in existing)
        {
            if (keepInstance is not null && ReferenceEquals(route.Instance, keepInstance))
                continue;
            session.DeleteInstance(StandardCimNamespace, route.Instance);
        }

        if (gateway is not null && keepInstance is null)
        {
            using var parameters = new CimMethodParametersCollection
            {
                CimMethodParameter.Create("InterfaceIndex", (uint)interfaceIndex, CimType.UInt32, CimFlags.None),
                CimMethodParameter.Create("DestinationPrefix", "0.0.0.0/0", CimType.String, CimFlags.None),
                CimMethodParameter.Create("NextHop", gateway, CimType.String, CimFlags.None),
                CimMethodParameter.Create("AddressFamily", (ushort)2, CimType.UInt16, CimFlags.None),
            };
            using var result = session.InvokeMethod(StandardCimNamespace, "MSFT_NetRoute", "Create", parameters);
            CheckReturn(result, $"MSFT_NetRoute.Create(0.0.0.0/0 via {gateway})");
        }

        foreach (var route in existing)
            route.Instance.Dispose();
    }

    public static void SetDnsServers(int interfaceIndex, IReadOnlyList<string> dnsServers)
    {
        using var session = CimSession.Create(null);

        // Group addresses by family so we can call SetDnsClientServerAddress
        // once per family — passing a mixed list confuses the provider.
        // Empty arrays reset that family back to DHCP-driven discovery.
        var byFamily = dnsServers
            .Select(s => (s, family: GetAddressFamily(s)))
            .Where(t => t.family is 2 or 23)
            .GroupBy(t => t.family);

        var familiesWritten = new HashSet<ushort>();
        foreach (var group in byFamily)
        {
            var family = (ushort)group.Key;
            familiesWritten.Add(family);
            InvokeSetDns(session, interfaceIndex, family, group.Select(t => t.s).ToArray());
        }

        // When the caller passes an empty desired list, we still need to
        // reset both families explicitly so previously-applied DNS doesn't
        // linger.
        if (dnsServers.Count == 0)
        {
            InvokeSetDns(session, interfaceIndex, 2, Array.Empty<string>());
            InvokeSetDns(session, interfaceIndex, 23, Array.Empty<string>());
        }
        else
        {
            // If the caller listed only IPv4 addresses, leave IPv6 alone
            // (and vice versa). We do NOT clear families the caller didn't
            // mention — that would surprise dual-stack guests.
            _ = familiesWritten;
        }
    }

    public static void SetInterfaceMtu(int interfaceIndex, int mtu)
    {
        using var session = CimSession.Create(null);

        foreach (var family in new ushort[] { 2, 23 })
        {
            var iface = FindIpInterface(session, interfaceIndex, family);
            if (iface is null)
                continue;

            using (iface)
            {
                var current = (uint?)iface.CimInstanceProperties["NlMtu"]?.Value;
                if (current == (uint)mtu)
                    continue;

                iface.CimInstanceProperties["NlMtu"].Value = (uint)mtu;
                session.ModifyInstance(StandardCimNamespace, iface);
            }
        }
    }

    // ---- helpers ----

    private static void InvokeSetDns(CimSession session, int interfaceIndex, ushort family, string[] servers)
    {
        using var parameters = new CimMethodParametersCollection
        {
            CimMethodParameter.Create("InterfaceIndex", (uint)interfaceIndex, CimType.UInt32, CimFlags.None),
            CimMethodParameter.Create("AddressFamily", family, CimType.UInt16, CimFlags.None),
            CimMethodParameter.Create(
                "ServerAddresses",
                servers,
                CimType.StringArray,
                servers.Length == 0 ? CimFlags.NullValue : CimFlags.None),
            CimMethodParameter.Create("ResetServerAddresses", servers.Length == 0, CimType.Boolean, CimFlags.None),
        };

        // MSFT_DNSClientServerAddress lives at the class level; the static
        // method SetServerAddress is the standard write path used by
        // Set-DnsClientServerAddress under the hood.
        using var result = session.InvokeMethod(
            StandardCimNamespace,
            "MSFT_DNSClientServerAddress",
            "SetServerAddress",
            parameters);
        CheckReturn(result, $"MSFT_DNSClientServerAddress.SetServerAddress(family={family})");
    }

    private static CimInstance? FindIpInterface(CimSession session, int interfaceIndex, ushort addressFamily)
    {
        var query = $"SELECT * FROM MSFT_NetIPInterface WHERE InterfaceIndex={interfaceIndex} AND AddressFamily={addressFamily}";
        return session.QueryInstances(StandardCimNamespace, "WQL", query).FirstOrDefault();
    }

    private readonly record struct IpAddressEntry(CimInstance Instance, string Address, byte PrefixLength);

    private static IEnumerable<IpAddressEntry> ListIpAddresses(CimSession session, int interfaceIndex, ushort addressFamily)
    {
        var query = $"SELECT * FROM MSFT_NetIPAddress WHERE InterfaceIndex={interfaceIndex} AND AddressFamily={addressFamily}";
        foreach (var instance in session.QueryInstances(StandardCimNamespace, "WQL", query))
        {
            var ip = GetString(instance, "IPAddress") ?? "";
            var prefix = (byte?)instance.CimInstanceProperties["PrefixLength"]?.Value ?? 0;
            yield return new IpAddressEntry(instance, ip, prefix);
        }
    }

    private static void RemoveDhcpAddresses(CimSession session, int interfaceIndex)
    {
        // PrefixOrigin=3 / SuffixOrigin=3 == DHCP. We delete those so the
        // manual addresses we apply next are unambiguous.
        var query = $"SELECT * FROM MSFT_NetIPAddress WHERE InterfaceIndex={interfaceIndex} AND PrefixOrigin=3";
        foreach (var instance in session.QueryInstances(StandardCimNamespace, "WQL", query))
        {
            using (instance)
                session.DeleteInstance(StandardCimNamespace, instance);
        }
    }

    private readonly record struct RouteEntry(CimInstance Instance, string NextHop);

    private static IEnumerable<RouteEntry> ListDefaultRoutes(CimSession session, int interfaceIndex, ushort addressFamily)
    {
        var dest = addressFamily == 2 ? "0.0.0.0/0" : "::/0";
        var query = $"SELECT * FROM MSFT_NetRoute WHERE InterfaceIndex={interfaceIndex} AND DestinationPrefix='{dest}'";
        foreach (var instance in session.QueryInstances(StandardCimNamespace, "WQL", query))
        {
            var next = GetString(instance, "NextHop") ?? "";
            yield return new RouteEntry(instance, next);
        }
    }

    private static void CheckReturn(CimMethodResult result, string operation)
    {
        var rc = (uint)(result.ReturnValue.Value ?? 0u);
        // The NetTCPIP CIM providers return 0 on success, non-zero on
        // failure. Standard error codes are documented under MSFT_NetIPAddress
        // and friends; surfacing the raw code is enough for triage — most are
        // ERROR_OBJECT_ALREADY_EXISTS (5023) or ERROR_NOT_FOUND (2).
        if (rc != 0)
            throw new InvalidOperationException(
                $"{operation} failed with CIM return code {rc}.");
    }

    private static uint GetUInt32(CimInstance instance, string property)
    {
        var value = instance.CimInstanceProperties[property]?.Value;
        return value switch
        {
            uint u => u,
            int i => (uint)i,
            ushort s => s,
            _ => 0u,
        };
    }

    private static string? GetString(CimInstance instance, string property) =>
        instance.CimInstanceProperties[property]?.Value as string;

    private static bool? GetBoolean(CimInstance instance, string property) =>
        instance.CimInstanceProperties[property]?.Value as bool?;

    private static string NormaliseMac(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        var hex = new char[12];
        var n = 0;
        foreach (var ch in raw)
        {
            if (n == 12) break;
            if (IsHexDigit(ch))
                hex[n++] = char.ToLowerInvariant(ch);
        }
        if (n != 12)
            return string.Empty;

        return string.Create(17, hex, (span, src) =>
        {
            for (var i = 0; i < 6; i++)
            {
                span[i * 3] = src[i * 2];
                span[i * 3 + 1] = src[i * 2 + 1];
                if (i < 5)
                    span[i * 3 + 2] = ':';
            }
        });
    }

    private static bool IsHexDigit(char c) =>
        (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

    private static (string Address, int Prefix) ParseCidr(string cidr)
    {
        var slash = cidr.IndexOf('/');
        if (slash < 0)
            throw new FormatException($"Address '{cidr}' is not in CIDR form.");
        var address = cidr[..slash];
        var prefix = int.Parse(cidr[(slash + 1)..], CultureInfo.InvariantCulture);
        return (address, prefix);
    }

    private static string NormaliseCidr(string cidr)
    {
        var (address, prefix) = ParseCidr(cidr);
        // Round-trip via IPAddress so "10.0.0.05" -> "10.0.0.5" etc.
        var ip = IPAddress.Parse(address);
        return $"{ip}/{prefix}";
    }

    private static ushort GetAddressFamily(string address)
    {
        if (!IPAddress.TryParse(address, out var ip))
            return 0;
        return ip.AddressFamily switch
        {
            System.Net.Sockets.AddressFamily.InterNetwork => 2,
            System.Net.Sockets.AddressFamily.InterNetworkV6 => 23,
            _ => 0,
        };
    }
}
