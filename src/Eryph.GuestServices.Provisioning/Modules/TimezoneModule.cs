using Eryph.GuestServices.Provisioning.Stages;
using Eryph.GuestServices.Provisioning.UserData;
using Microsoft.Extensions.Logging;

namespace Eryph.GuestServices.Provisioning.Modules;

/// <summary>
/// Implements cloud-init's <c>cc_timezone</c> on Windows. Accepts either an
/// IANA timezone (<c>Europe/Berlin</c>) or a Windows timezone key name
/// (<c>W. Europe Standard Time</c>) and applies it via <c>tzutil</c>. The
/// IANA→Windows translation is done by <see cref="TimeZoneInfo"/>; .NET 8+
/// ships the CLDR mapping in the runtime so we don't need our own table.
/// </summary>
[Stage(Stage.Network, Order = 4, Frequency = ModuleFrequency.PerInstance)]
internal sealed class TimezoneModule(ILogger<TimezoneModule> logger) : IModule
{
    public async Task<ModuleOutcome> ApplyAsync(
        ResolvedUserData userData,
        IModuleContext context,
        CancellationToken cancellationToken)
    {
        var raw = userData.CloudConfig.Timezone;
        if (string.IsNullOrWhiteSpace(raw))
        {
            logger.LogDebug("No timezone in cloud-config; leaving system zone alone.");
            return ModuleOutcome.Ok();
        }

        var requested = raw.Trim();
        if (!TryResolveWindowsTimezoneId(requested, out var windowsId))
        {
            logger.LogError(
                "Cannot resolve timezone {Raw} to a Windows zone id; refusing to apply.",
                requested);
            return ModuleOutcome.Fail($"timezone: unknown id '{requested}'");
        }

        try
        {
            await context.Os.SetTimezoneAsync(windowsId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to set timezone {WindowsId}.", windowsId);
            return ModuleOutcome.Fail($"timezone: {ex.Message}", ex);
        }

        logger.LogInformation(
            "Timezone applied: {Raw} -> {WindowsId}.", requested, windowsId);
        return ModuleOutcome.Ok();
    }

    private static bool TryResolveWindowsTimezoneId(string value, out string windowsId)
    {
        // If the caller already gave us a Windows zone id, accept it as-is.
        try
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById(value);
            // FindSystemTimeZoneById accepts both Windows and IANA ids on .NET 8+
            // — normalise to the Windows form before tzutil sees it.
            if (TimeZoneInfo.TryConvertIanaIdToWindowsId(tz.Id, out var converted))
            {
                windowsId = converted;
                return true;
            }
            windowsId = tz.Id;
            return true;
        }
        catch (TimeZoneNotFoundException)
        {
            // Last-ditch attempt: explicit IANA→Windows conversion in case the
            // local TZ database lookup did not find it but the static mapping does.
            if (TimeZoneInfo.TryConvertIanaIdToWindowsId(value, out var converted))
            {
                windowsId = converted;
                return true;
            }
            windowsId = string.Empty;
            return false;
        }
        catch (InvalidTimeZoneException)
        {
            windowsId = string.Empty;
            return false;
        }
    }
}
