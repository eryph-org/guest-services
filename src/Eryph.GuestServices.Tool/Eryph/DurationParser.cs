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

        // A user can type an absurdly large component ("999999999999d"). That is
        // just an invalid duration, not an exception: int.TryParse rejects the
        // overflow and the TimeSpan constructor is guarded below.
        if (!TryParseGroup(match, "d", out var days)
            || !TryParseGroup(match, "h", out var hours)
            || !TryParseGroup(match, "m", out var minutes)
            || !TryParseGroup(match, "s", out var seconds))
            return false;

        try
        {
            duration = new TimeSpan(days, hours, minutes, seconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            duration = TimeSpan.Zero;
            return false;
        }

        return duration > TimeSpan.Zero;
    }

    private static bool TryParseGroup(Match match, string name, out int value)
    {
        value = 0;
        return !match.Groups[name].Success
            || int.TryParse(match.Groups[name].Value, NumberStyles.None,
                CultureInfo.InvariantCulture, out value);
    }
}
