using Eryph.GuestServices.CloudConfig;
using Eryph.GuestServices.Provisioning.Stages;
using Eryph.GuestServices.Provisioning.UserData;
using Microsoft.Extensions.Logging;

namespace Eryph.GuestServices.Provisioning.Modules;

/// <summary>
/// Mirrors cloudbase-init's <c>NTPClientPlugin</c> and the
/// platform-independent parts of cloud-init's <c>cc_ntp</c>: configures the
/// Windows Time service (<c>w32time</c>) and seeds its manual peer list
/// from <c>ntp.servers</c> + <c>ntp.pools</c>.
/// </summary>
[Stage(Stage.Network, Order = 3, Frequency = ModuleFrequency.PerInstance)]
internal sealed class NtpClientModule(ILogger<NtpClientModule> logger) : IModule
{
    public async Task<ModuleOutcome> ApplyAsync(
        ResolvedUserData userData,
        IModuleContext context,
        CancellationToken cancellationToken)
    {
        var ntp = userData.CloudConfig.Ntp;
        if (ntp is null)
        {
            logger.LogDebug("No ntp block; leaving w32time configuration alone.");
            return ModuleOutcome.Ok();
        }

        var enabled = ntp.Enabled ?? true;
        var peers = MergePeers(ntp.Servers, ntp.Pools);

        if (enabled && peers.Count == 0)
        {
            // cloud-init treats `ntp: { enabled: true }` with no peers as a
            // "ensure service is running" directive. We do the same — keeping
            // the existing peer list (typically DHCP-provided or the OS default).
            logger.LogInformation("NTP enabled with no explicit peers; ensuring w32time is running.");
        }

        // RTC-UTC is applied BEFORE the service config so a subsequent w32time
        // sync sees the right interpretation of the hardware clock. Only
        // touch the registry when the operator was explicit — Windows defaults
        // to "RTC is local time" and rewriting the value on every run would
        // be a needless reboot trigger on systems that don't want it.
        if (ntp.RealTimeClockUtc.HasValue)
        {
            try
            {
                await context.Os
                    .SetRealTimeClockUtcAsync(ntp.RealTimeClockUtc.Value, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "RealTimeIsUniversal write failed.");
                return ModuleOutcome.Fail($"ntp: {ex.Message}", ex);
            }
        }

        try
        {
            await context.Os.ConfigureNtpClientAsync(enabled, peers, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "NTP configuration failed.");
            return ModuleOutcome.Fail($"ntp: {ex.Message}", ex);
        }

        logger.LogInformation(
            "NTP configured (enabled={Enabled}, peers={Peers}, rtcUtc={Rtc}).",
            enabled,
            peers.Count == 0 ? "<none>" : string.Join(", ", peers),
            ntp.RealTimeClockUtc?.ToString() ?? "<unchanged>");
        return ModuleOutcome.Ok();
    }

    // Cloud-init keeps `servers` and `pools` as distinct concepts because
    // Linux NTP clients differentiate (a pool resolves to multiple servers
    // via SRV records, while servers are direct). w32time has no such
    // distinction — both arrive at /manualpeerlist verbatim. We preserve
    // input order and drop empties.
    private static IReadOnlyList<string> MergePeers(
        IReadOnlyList<string>? servers,
        IReadOnlyList<string>? pools)
    {
        var result = new List<string>();
        if (servers is not null)
            foreach (var s in servers)
                if (!string.IsNullOrWhiteSpace(s))
                    result.Add(s.Trim());
        if (pools is not null)
            foreach (var p in pools)
                if (!string.IsNullOrWhiteSpace(p))
                    result.Add(p.Trim());
        return result;
    }
}
