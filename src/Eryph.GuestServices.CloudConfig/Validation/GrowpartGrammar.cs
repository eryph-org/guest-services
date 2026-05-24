using LanguageExt;
using LanguageExt.Common;

using static LanguageExt.Prelude;

namespace Eryph.GuestServices.CloudConfig.Validation;

/// <summary>
/// Schema-level parsing + validation for <see cref="GrowpartConfig"/>.
/// </summary>
public static class GrowpartGrammar
{
    /// <summary>
    /// Possible runtime targets for a parsed <c>devices:</c> entry.
    /// </summary>
    public enum DeviceKind
    {
        /// <summary>The cloud-init root device alias <c>/</c>.</summary>
        SystemDrive,
        /// <summary>An explicit drive letter (uppercase A–Z).</summary>
        DriveLetter,
        /// <summary>The catch-all <c>all</c> sentinel.</summary>
        All,
    }

    public readonly record struct DeviceTarget(DeviceKind Kind, char? DriveLetter);

    /// <summary>
    /// Parse a single <c>devices:</c> entry. Empty / whitespace is treated
    /// as missing (callers may filter it out).
    /// </summary>
    public static Validation<Error, DeviceTarget> ParseDevice(string raw)
    {
        var trimmed = (raw ?? string.Empty).Trim();
        if (trimmed.Length == 0)
            return Fail<Error, DeviceTarget>(Error.New(
                "Device entry is empty. Valid values: '/', 'all', or a drive letter ('C', 'C:', or 'C:\\\\')."));

        if (string.Equals(trimmed, "all", StringComparison.OrdinalIgnoreCase))
            return Success<Error, DeviceTarget>(new DeviceTarget(DeviceKind.All, null));

        // Cloud-init's canonical "root device" is "/" — on Windows that
        // is the system drive (where Windows is installed). The backslash
        // form is accepted for operators thinking in Windows terms.
        if (trimmed is "/" or "\\")
            return Success<Error, DeviceTarget>(new DeviceTarget(DeviceKind.SystemDrive, null));

        var letter = TryExtractDriveLetter(trimmed);
        if (letter is not null)
            return Success<Error, DeviceTarget>(new DeviceTarget(DeviceKind.DriveLetter, letter));

        return Fail<Error, DeviceTarget>(Error.New(
            $"Device entry '{raw}' is invalid. "
            + "Valid forms: '/', 'all', a drive letter ('C'), or a drive letter with colon ('C:' or 'C:\\\\') — quote forms containing a colon."));
    }

    private static char? TryExtractDriveLetter(string value)
    {
        // Accept "C", "C:", "C:\\", "C:/", "c:" — any ASCII letter
        // optionally followed by ':' and a single trailing path separator.
        if (value.Length == 0) return null;
        var ch = value[0];
        if (ch is >= 'a' and <= 'z') ch = char.ToUpperInvariant(ch);
        if (ch is < 'A' or > 'Z') return null;
        if (value.Length == 1) return ch;
        if (value[1] != ':') return null;
        if (value.Length == 2) return ch;
        if (value.Length == 3 && (value[2] == '\\' || value[2] == '/')) return ch;
        return null;
    }

    /// <summary>
    /// Cloud-config-shape validator. Returns Unit on success or accumulated
    /// errors. Wires into <c>CloudConfigValidations.ValidateCloudConfig</c>.
    /// </summary>
    public static Validation<Error, Unit> Validate(GrowpartConfig config)
    {
        // Mode is a small enum — keep the rule co-located instead of a
        // separate static. cloud-init accepts the boolean `false` (YAML),
        // which the YAML schema resolver hands us as `bool` and so does
        // not land here — we only see string variants.
        var modeValidation = config.Mode switch
        {
            null => Success<Error, Unit>(unit),
            var s when string.Equals(s, "auto", StringComparison.OrdinalIgnoreCase) => Success<Error, Unit>(unit),
            var s when string.Equals(s, "off", StringComparison.OrdinalIgnoreCase) => Success<Error, Unit>(unit),
            var s when string.Equals(s, "false", StringComparison.OrdinalIgnoreCase) => Success<Error, Unit>(unit),
            _ => Fail<Error, Unit>(Error.New(
                $"mode '{config.Mode}' is invalid. Valid values: auto, off, false.")),
        };

        var devicesValidation = config.Devices is null
            ? Success<Error, Unit>(unit)
            : config.Devices.ToSeq()
                .Map((i, d) => ParseDevice(d ?? string.Empty)
                    .Map(_ => unit)
                    .MapFail(err => Error.New($"devices[{i}]: {err.Message}", err)))
                .Sequence()
                .Map(_ => unit);

        return (modeValidation | devicesValidation).Map(_ => unit);
    }
}
