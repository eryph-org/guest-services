using System.Globalization;
using System.Text.RegularExpressions;

namespace Eryph.GuestServices.Tool.Eryph;

// Parses a short human duration such as "8h", "30m", "90s", "2d" or a combined
// "1d12h30m" into a TimeSpan. Used by the add-key TTL option to turn a relative
// duration into an absolute expiry timestamp. A plain number with no unit is
// rejected: an ambiguous unit-less TTL is a likely operator mistake (seconds vs
// minutes vs hours) and silently guessing would set the wrong expiry.
public static partial class DurationParser
{
    [GeneratedRegex(@"^\s*(?:(?<d>\d+)d)?(?:(?<h>\d+)h)?(?:(?<m>\d+)m)?(?:(?<s>\d+)s)?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DurationRegex();

    public static bool TryParse(string? value, out TimeSpan duration)
    {
        duration = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var match = DurationRegex().Match(value);
        // The pattern matches an empty string (every group optional), so reject
        // a match that captured no unit at all.
        if (!match.Success
            || !(match.Groups["d"].Success || match.Groups["h"].Success
                 || match.Groups["m"].Success || match.Groups["s"].Success))
            return false;

        var days = ParseGroup(match, "d");
        var hours = ParseGroup(match, "h");
        var minutes = ParseGroup(match, "m");
        var seconds = ParseGroup(match, "s");

        duration = new TimeSpan(days, hours, minutes, seconds);
        return duration > TimeSpan.Zero;
    }

    private static int ParseGroup(Match match, string name) =>
        match.Groups[name].Success
            ? int.Parse(match.Groups[name].Value, CultureInfo.InvariantCulture)
            : 0;
}
