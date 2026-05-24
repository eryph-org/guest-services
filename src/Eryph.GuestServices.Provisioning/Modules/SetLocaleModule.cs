using Eryph.GuestServices.Provisioning.Stages;
using Eryph.GuestServices.Provisioning.UserData;
using Eryph.GuestServices.Provisioning.Windows;
using Microsoft.Extensions.Logging;

namespace Eryph.GuestServices.Provisioning.Modules;

/// <summary>
/// Implements cloud-init's <c>cc_set_locale</c> and <c>cc_keyboard</c> on
/// Windows. The <c>locale</c> scalar (e.g. <c>de-DE</c>) drives culture,
/// UI language and the user language list; <c>keyboard.layout</c> drives
/// the input method tip. Changing the SYSTEM locale (Win32 ANSI codepage)
/// requires a reboot — surfaced via <see cref="ModuleOutcome.RebootRequested"/>.
/// </summary>
[Stage(Stage.Network, Order = 5, Frequency = ModuleFrequency.PerInstance)]
internal sealed class SetLocaleModule(ILogger<SetLocaleModule> logger) : IModule
{
    public async Task<ModuleOutcome> ApplyAsync(
        ResolvedUserData userData,
        IModuleContext context,
        CancellationToken cancellationToken)
    {
        var locale = string.IsNullOrWhiteSpace(userData.CloudConfig.Locale)
            ? null
            : userData.CloudConfig.Locale.Trim();
        var keyboard = string.IsNullOrWhiteSpace(userData.CloudConfig.Keyboard?.Layout)
            ? null
            : userData.CloudConfig.Keyboard!.Layout!.Trim();

        if (locale is null && keyboard is null)
        {
            logger.LogDebug("No locale or keyboard in cloud-config; nothing to do.");
            return ModuleOutcome.Ok();
        }

        var spec = new LocaleSpec { Locale = locale, KeyboardLayout = keyboard };

        LocaleApplyResult result;
        try
        {
            result = await context.Os.ApplyLocaleAsync(spec, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Locale apply failed.");
            return ModuleOutcome.Fail($"locale: {ex.Message}", ex);
        }

        if (result.RebootRequired)
        {
            logger.LogInformation(
                "Locale {Locale} applied; reboot required for system locale change.", locale);
            return ModuleOutcome.Reboot($"System locale change to '{locale}' requires reboot.");
        }

        logger.LogInformation(
            "Locale applied (locale={Locale}, keyboard={Keyboard}).",
            locale ?? "<unchanged>", keyboard ?? "<unchanged>");
        return ModuleOutcome.Ok();
    }
}
