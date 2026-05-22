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
        var byMac = adapters
            .Where(a => a.IsPhysical && !string.IsNullOrEmpty(a.MacAddress))
            .ToDictionary(a => a.MacAddress, StringComparer.OrdinalIgnoreCase);

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
                    "Network-config entry '{Name}' references MAC {Mac} but no matching physical adapter is present; skipping.",
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
        // DHCP-only is the OS default on a fresh adapter; treat it as a no-op
        // for IPv4 even when the schema spells it explicitly. We do still
        // honour MTU below.
        var isDhcpOnly = ethernet.Dhcp4 == true
            && (ethernet.Addresses is null || ethernet.Addresses.Count == 0);

        if (isDhcpOnly)
        {
            logger.LogInformation(
                "Adapter '{Alias}' ({Mac}): DHCP requested — leaving IPv4 alone.",
                adapter.InterfaceAlias, adapter.MacAddress);

            // Ensure DHCP is actually on; previous runs may have disabled it.
            await context.Os.EnableDhcpAsync(adapter.InterfaceIndex, cancellationToken).ConfigureAwait(false);
        }
        else if (ethernet.Addresses is { Count: > 0 })
        {
            logger.LogInformation(
                "Adapter '{Alias}' ({Mac}): applying static addresses {Addresses}.",
                adapter.InterfaceAlias, adapter.MacAddress, string.Join(", ", ethernet.Addresses));

            // Order matters here: disable DHCP before adding static addresses
            // so the manual addresses are the only configured ones. The
            // gateway must come last because it references the freshly-added
            // interface route.
            await context.Os.DisableDhcpAsync(adapter.InterfaceIndex, cancellationToken).ConfigureAwait(false);
            await context.Os.SetStaticIpv4AddressesAsync(
                adapter.InterfaceIndex, ethernet.Addresses, cancellationToken).ConfigureAwait(false);
            await context.Os.SetIpv4DefaultGatewayAsync(
                adapter.InterfaceIndex, ethernet.Gateway4, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            // No addresses and not DHCP — treat as "leave IPv4 untouched" but
            // still apply DNS / MTU below. This matches cloud-init's behaviour
            // for an ethernet with only nameservers / mtu / routes specified.
            logger.LogInformation(
                "Adapter '{Alias}' ({Mac}): no IPv4 directive; leaving addresses untouched.",
                adapter.InterfaceAlias, adapter.MacAddress);
        }

        if (ethernet.Nameservers?.Addresses is { Count: > 0 } dns)
        {
            logger.LogInformation(
                "Adapter '{Alias}' ({Mac}): setting DNS servers {Servers}.",
                adapter.InterfaceAlias, adapter.MacAddress, string.Join(", ", dns));
            await context.Os.SetDnsServersAsync(
                adapter.InterfaceIndex, dns, cancellationToken).ConfigureAwait(false);
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
