using System.Globalization;
using LanguageExt;
using LanguageExt.Common;
using EryphValidations = Eryph.ConfigModel.Validations;

using static LanguageExt.Prelude;

namespace Eryph.GuestServices.CloudConfig.Validation;

/// <summary>
/// Schema-level parsing + validation for <see cref="PowerStateConfig"/>.
/// Lives in the model library so <c>egs-service validate</c> can reject
/// bad config BEFORE the agent runs — the alternative (parsing in
/// <c>PowerStateModule</c>) defers errors to first boot.
/// </summary>
public static class PowerStateGrammar
{
    public enum PowerStateMode
    {
        Reboot = 0,
        Poweroff = 1,
        Halt = 2,
    }

    /// <summary>
    /// Resolve a raw <c>mode:</c> string to the canonical <see cref="PowerStateMode"/>.
    /// Accepts <c>reboot</c>, <c>poweroff</c>, <c>shutdown</c> (cbi-style
    /// alias), <c>halt</c>. Null / empty → <see cref="PowerStateMode.Reboot"/>
    /// (operators writing <c>power_state: {}</c> almost always want reboot).
    /// </summary>
    public static Validation<Error, PowerStateMode> ParseMode(string? raw)
    {
        var normalized = string.IsNullOrWhiteSpace(raw) ? "reboot" : raw.Trim().ToLowerInvariant();
        return normalized switch
        {
            "reboot" => Success<Error, PowerStateMode>(PowerStateMode.Reboot),
            "poweroff" or "shutdown" => Success<Error, PowerStateMode>(PowerStateMode.Poweroff),
            "halt" => Success<Error, PowerStateMode>(PowerStateMode.Halt),
            _ => Fail<Error, PowerStateMode>(Error.New(
                $"The mode '{raw}' is invalid. Valid values: reboot, poweroff (or 'shutdown'), halt.")),
        };
    }

    /// <summary>
    /// Parse a cloud-init style <c>delay:</c> string into seconds-from-now.
    /// Forms: <c>now</c> / empty / null → 0; <c>+N</c> → N minutes;
    /// <c>HH:MM</c> → seconds until that local-time today (tomorrow if past);
    /// plain integer → seconds.
    /// </summary>
    public static Validation<Error, int> ParseDelay(string? raw, DateTimeOffset now)
    {
        var s = (raw ?? "now").Trim();
        if (string.IsNullOrEmpty(s) || s.Equals("now", StringComparison.OrdinalIgnoreCase))
            return Success<Error, int>(0);

        // "+N" → N minutes from now (cloud-init shape).
        if (s.StartsWith('+'))
        {
            return int.TryParse(s[1..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var mins) && mins >= 0
                ? Success<Error, int>(mins * 60)
                : Fail<Error, int>(Error.New(
                    $"The delay '{raw}' is invalid. After '+' the value must be a non-negative integer (minutes)."));
        }

        // Plain integer → seconds from now.
        if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var secs) && secs >= 0)
            return Success<Error, int>(secs);

        // "HH:MM" absolute time today (tomorrow if past now).
        if (s.Length == 5 && s[2] == ':'
            && int.TryParse(s[..2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var h)
            && int.TryParse(s[3..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var m)
            && h is >= 0 and < 24 && m is >= 0 and < 60)
        {
            var todayLocal = TimeZoneInfo.ConvertTime(now, TimeZoneInfo.Local);
            var target = new DateTimeOffset(todayLocal.Year, todayLocal.Month, todayLocal.Day, h, m, 0, todayLocal.Offset);
            if (target <= todayLocal) target = target.AddDays(1);
            var deltaSeconds = (int)Math.Round((target - todayLocal).TotalSeconds);
            return Success<Error, int>(Math.Max(0, deltaSeconds));
        }

        return Fail<Error, int>(Error.New(
            $"The delay '{raw}' is invalid. Valid forms: 'now', '+N' (minutes), 'HH:MM' (24-hour), or an integer (seconds)."));
    }

    /// <summary>
    /// Cloud-config-shape validator. Returns Unit on success or accumulated
    /// errors. Wires into <c>CloudConfigValidations.ValidateCloudConfig</c>.
    /// </summary>
    public static Validation<Error, Unit> Validate(PowerStateConfig config) =>
        (ParseMode(config.Mode).Map(_ => unit).MapFail(e => Error.New($"mode: {e.Message}", e))
            | ParseDelay(config.Delay, DateTimeOffset.UtcNow).Map(_ => unit).MapFail(e => Error.New($"delay: {e.Message}", e))
            | (config.Message is null
                ? Success<Error, Unit>(unit)
                : EryphValidations.ValidateLength(config.Message, "message", 0, 512).Map(_ => unit)))
        .Map(_ => unit);
}
