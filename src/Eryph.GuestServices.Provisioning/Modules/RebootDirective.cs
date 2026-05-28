using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Eryph.GuestServices.Provisioning.Modules;

/// <summary>
/// Shared helpers for the cbi-style reboot contract used by both
/// <see cref="RuncmdModule"/> and <see cref="ScriptsUserModule"/>: the env
/// vars surfaced to the child process, and the parsing of the script-emitted
/// <c>##egs.reboot_limit=&lt;n&gt;</c> directive that raises the per-script
/// reboot cap.
/// </summary>
internal static class RebootEnvVars
{
    public const string EntryIndex = "EGS_ENTRY_INDEX";
    public const string RebootCount = "EGS_REBOOT_COUNT";
    public const string RebootLimit = "EGS_REBOOT_LIMIT";

    public static IReadOnlyDictionary<string, string> Build(int ordinal, int rebootCount, int rebootLimit) =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [EntryIndex] = ordinal.ToString(CultureInfo.InvariantCulture),
            [RebootCount] = rebootCount.ToString(CultureInfo.InvariantCulture),
            [RebootLimit] = rebootLimit.ToString(CultureInfo.InvariantCulture),
        };
}

internal static partial class RebootDirective
{
    public const string Token = "##egs.reboot_limit";

    /// <summary>
    /// Returns the script-supplied new reboot limit when the entry's stdout
    /// contains a <c>##egs.reboot_limit=&lt;n&gt;</c> line that would RAISE
    /// the current cap. Returns null when the script emitted nothing, when
    /// overrides are disabled, or when the emitted value would lower or
    /// equal the current cap (lowering is logged and ignored to keep the
    /// contract monotonic; equal is a no-op).
    /// </summary>
    public static int? ParseRaise(
        string stdout,
        int currentLimit,
        bool allowOverride,
        int ordinal,
        string moduleLabel,
        ILogger logger)
    {
        if (!allowOverride)
            return null;
        var emitted = Extract(stdout);
        if (emitted is null)
            return null;
        if (emitted <= currentLimit)
        {
            if (emitted < currentLimit)
                logger.LogWarning(
                    "{Module} #{Index} emitted {Token}={Emitted} but the directive only raises the limit (current {Current}); ignoring.",
                    moduleLabel, ordinal, Token, emitted, currentLimit);
            return null;
        }
        logger.LogInformation(
            "{Module} #{Index} raised its per-script reboot limit to {NewLimit} via {Token} directive.",
            moduleLabel, ordinal, emitted, Token);
        return emitted;
    }

    private static int? Extract(string output)
    {
        if (string.IsNullOrEmpty(output))
            return null;
        int? last = null;
        foreach (Match match in LimitDirectiveRegex().Matches(output))
        {
            if (int.TryParse(
                    match.Groups[1].Value,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var value)
                && value > 0)
            {
                last = value;
            }
        }
        return last;
    }

    // The "##" prefix disambiguates the directive from a shell KEY=VALUE
    // line so a script that prints its environment cannot accidentally
    // raise its own limit. [0-9]+ (not \d+) restricts to ASCII digits so
    // int.TryParse with InvariantCulture always succeeds on a match.
    [GeneratedRegex(@"^\s*##egs\.reboot_limit\s*=\s*([0-9]+)\s*$",
        RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex LimitDirectiveRegex();
}
