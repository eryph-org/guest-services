using Eryph.GuestServices.CloudConfig;
using Eryph.GuestServices.Provisioning.Stages;
using Eryph.GuestServices.Provisioning.UserData;
using Eryph.GuestServices.Provisioning.Windows;
using Microsoft.Extensions.Logging;

namespace Eryph.GuestServices.Provisioning.Modules;

/// <summary>
/// Applies cloud-init network-config (v1 or v2) to the Windows guest. Matches
/// adapters by MAC address; falls back to no-op for any adapter we cannot
/// resolve. See <c>docs/rfcs/0002</c> for the schema mapping.
/// </summary>
/// <remarks>
/// The module is idempotent: a re-run with the same input produces the same
/// final state. It runs in the Network stage (<see cref="Stage.Network"/>)
/// after <c>SetHostnameModule</c> (Order=1) so the hostname change isn't
/// disturbed by a possible network drop during DHCP-to-static transitions.
/// </remarks>
[Stage(Stage.Network, Order = 2, Frequency = ModuleFrequency.PerInstance)]
internal sealed class ApplyNetworkConfigModule(ILogger<ApplyNetworkConfigModule> logger) : IModule
{
    public async Task<ModuleOutcome> ApplyAsync(
        ResolvedUserData userData,
        IModuleContext context,
        CancellationToken cancellationToken)
    {
        // network-config lives on the datasource, not in the user-data
        // CloudConfig. ResolvedUserData carries the latter; the former is
        // surfaced via IModuleContext.DataSource.StructuredNetworkConfig.
        var network = context.DataSource.StructuredNetworkConfig;
        if (network is null || network.Ethernets is null || network.Ethernets.Count == 0)
        {
            logger.LogDebug("No network-config ethernets to apply; nothing to do.");
            return ModuleOutcome.Ok();
        }

        var adapters = await context.Os.GetNetworkAdaptersAsync(cancellationToken).ConfigureAwait(false);
        // The enumeration only returns MAC-bearing NICs; match by exact MAC.
        // Group rather than ToDictionary so two adapters sharing a MAC (e.g. a
        // NIC team and its member) can't throw and block the whole config; the
        // lowest interface index wins deterministically.
        var byMac = adapters
            .Where(a => !string.IsNullOrEmpty(a.MacAddress))
            .GroupBy(a => a.MacAddress, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(a => a.InterfaceIndex).First(),
                StringComparer.OrdinalIgnoreCase);

        foreach (var (key, ethernet) in network.Ethernets)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var mac = NormaliseMac(ethernet.MacAddress);
            if (string.IsNullOrEmpty(mac))
            {
                // v1 sometimes omits mac_address; we have no way to bind that
                // entry to a specific Windows adapter, so log and continue
                // (matching cloud-init's "skip unmatched" semantics).
                logger.LogWarning(
                    "Network-config entry '{Name}' has no MAC address; skipping (Windows requires MAC matching).",
                    key);
                continue;
            }

            if (!byMac.TryGetValue(mac, out var adapter))
            {
                logger.LogWarning(
                    "Network-config entry '{Name}' references MAC {Mac} but no matching adapter is present; skipping.",
                    key, mac);
                continue;
            }

            await ApplyToAdapter(context, adapter, ethernet, cancellationToken).ConfigureAwait(false);
        }

        return ModuleOutcome.Ok();
    }

    private async Task ApplyToAdapter(
        IModuleContext context,
        NetworkAdapterInfo adapter,
        NetworkEthernetConfig ethernet,
        CancellationToken cancellationToken)
    {
        // Split addresses by family so IPv4 and IPv6 entries flow through the
        // appropriate OS calls. cloud-init's `addresses:` list mixes families
        // and we honour that.
        var (v4Addresses, v6Addresses) = SplitAddressesByFamily(ethernet.Addresses);

        await ApplyIpv4Async(context, adapter, ethernet, v4Addresses, cancellationToken).ConfigureAwait(false);
        await ApplyIpv6Async(context, adapter, ethernet, v6Addresses, cancellationToken).ConfigureAwait(false);

        if (ethernet.Routes is { Count: > 0 } routes)
        {
            logger.LogInformation(
                "Adapter '{Alias}' ({Mac}): applying {Count} explicit route(s).",
                adapter.InterfaceAlias, adapter.MacAddress, routes.Count);
            await context.Os.SetInterfaceRoutesAsync(
                adapter.InterfaceIndex, routes, cancellationToken).ConfigureAwait(false);
        }

        if (ethernet.Nameservers?.Addresses is { Count: > 0 } dns)
        {
            logger.LogInformation(
                "Adapter '{Alias}' ({Mac}): setting DNS servers {Servers}.",
                adapter.InterfaceAlias, adapter.MacAddress, string.Join(", ", dns));
            await context.Os.SetDnsServersAsync(
                adapter.InterfaceIndex, dns, cancellationToken).ConfigureAwait(false);
        }

        if (ethernet.Nameservers?.Search is { Count: > 0 } search)
        {
            logger.LogInformation(
                "Adapter '{Alias}' ({Mac}): setting DNS search suffixes {Suffixes}.",
                adapter.InterfaceAlias, adapter.MacAddress, string.Join(", ", search));
            await context.Os.SetDnsSearchSuffixesAsync(
                adapter.InterfaceIndex, search, cancellationToken).ConfigureAwait(false);
        }

        if (ethernet.Mtu is int mtu && mtu > 0)
        {
            logger.LogInformation(
                "Adapter '{Alias}' ({Mac}): setting MTU {Mtu}.",
                adapter.InterfaceAlias, adapter.MacAddress, mtu);
            await context.Os.SetInterfaceMtuAsync(
                adapter.InterfaceIndex, mtu, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ApplyIpv4Async(
        IModuleContext context,
        NetworkAdapterInfo adapter,
        NetworkEthernetConfig ethernet,
        IReadOnlyList<string> v4Addresses,
        CancellationToken cancellationToken)
    {
        // DHCP-only is the OS default on a fresh adapter; treat it as a no-op
        // for IPv4 even when the schema spells it explicitly. We still honour
        // MTU + DNS later in the caller.
        var isDhcpOnly = ethernet.Dhcp4 == true && v4Addresses.Count == 0;

        if (isDhcpOnly)
        {
            logger.LogInformation(
                "Adapter '{Alias}' ({Mac}): DHCP requested — leaving IPv4 alone.",
                adapter.InterfaceAlias, adapter.MacAddress);

            // Ensure DHCP is actually on; previous runs may have disabled it.
            await context.Os.EnableDhcpAsync(adapter.InterfaceIndex, cancellationToken).ConfigureAwait(false);
        }
        else if (v4Addresses.Count > 0)
        {
            logger.LogInformation(
                "Adapter '{Alias}' ({Mac}): applying static IPv4 addresses {Addresses}.",
                adapter.InterfaceAlias, adapter.MacAddress, string.Join(", ", v4Addresses));

            // Order matters: disable DHCP before adding static addresses so
            // the manual set is the only configured one. The gateway must come
            // last because it references the freshly-added interface route.
            await context.Os.DisableDhcpAsync(adapter.InterfaceIndex, cancellationToken).ConfigureAwait(false);
            await context.Os.SetStaticIpv4AddressesAsync(
                adapter.InterfaceIndex, v4Addresses, cancellationToken).ConfigureAwait(false);
            await context.Os.SetIpv4DefaultGatewayAsync(
                adapter.InterfaceIndex, ethernet.Gateway4, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            // No v4 directive — leave the family alone. Matches cloud-init's
            // behaviour for an ethernet with only nameservers / mtu / routes
            // / IPv6 specified.
            logger.LogDebug(
                "Adapter '{Alias}' ({Mac}): no IPv4 directive; leaving IPv4 untouched.",
                adapter.InterfaceAlias, adapter.MacAddress);
        }
    }

    private async Task ApplyIpv6Async(
        IModuleContext context,
        NetworkAdapterInfo adapter,
        NetworkEthernetConfig ethernet,
        IReadOnlyList<string> v6Addresses,
        CancellationToken cancellationToken)
    {
        var isDhcp6Only = ethernet.Dhcp6 == true && v6Addresses.Count == 0;

        if (isDhcp6Only)
        {
            logger.LogInformation(
                "Adapter '{Alias}' ({Mac}): DHCPv6 requested — leaving IPv6 alone.",
                adapter.InterfaceAlias, adapter.MacAddress);
            await context.Os.EnableDhcp6Async(adapter.InterfaceIndex, cancellationToken).ConfigureAwait(false);
        }
        else if (v6Addresses.Count > 0)
        {
            logger.LogInformation(
                "Adapter '{Alias}' ({Mac}): applying static IPv6 addresses {Addresses}.",
                adapter.InterfaceAlias, adapter.MacAddress, string.Join(", ", v6Addresses));

            await context.Os.DisableDhcp6Async(adapter.InterfaceIndex, cancellationToken).ConfigureAwait(false);
            await context.Os.SetStaticIpv6AddressesAsync(
                adapter.InterfaceIndex, v6Addresses, cancellationToken).ConfigureAwait(false);
            await context.Os.SetIpv6DefaultGatewayAsync(
                adapter.InterfaceIndex, ethernet.Gateway6, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            logger.LogDebug(
                "Adapter '{Alias}' ({Mac}): no IPv6 directive; leaving IPv6 untouched.",
                adapter.InterfaceAlias, adapter.MacAddress);
        }
    }

    // The presence of a ':' in the address part is a reliable family
    // discriminator: IPv4 CIDRs never contain ':', IPv6 CIDRs always do.
    private static (IReadOnlyList<string> V4, IReadOnlyList<string> V6) SplitAddressesByFamily(
        IReadOnlyList<string>? addresses)
    {
        if (addresses is null || addresses.Count == 0)
            return (Array.Empty<string>(), Array.Empty<string>());

        var v4 = new List<string>();
        var v6 = new List<string>();
        foreach (var a in addresses)
        {
            if (string.IsNullOrWhiteSpace(a))
                continue;
            if (a.Contains(':'))
                v6.Add(a);
            else
                v4.Add(a);
        }
        return (v4, v6);
    }

    // Normalise to colon-separated lowercase 6-byte form so v1's "d2:ab:.."
    // and v2's "D2-AB-.." both match the canonical adapter MAC.
    private static string NormaliseMac(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        Span<char> hex = stackalloc char[12];
        var n = 0;
        foreach (var ch in raw)
        {
            if (n == 12) break;
            if (IsHexDigit(ch))
                hex[n++] = char.ToLowerInvariant(ch);
        }
        if (n != 12)
            return string.Empty;

        Span<char> buffer = stackalloc char[17];
        for (var i = 0; i < 6; i++)
        {
            buffer[i * 3] = hex[i * 2];
            buffer[i * 3 + 1] = hex[i * 2 + 1];
            if (i < 5)
                buffer[i * 3 + 2] = ':';
        }
        return new string(buffer);
    }

    private static bool IsHexDigit(char c) =>
        (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
}
