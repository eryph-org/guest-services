using System.Globalization;

namespace Eryph.GuestServices.CloudConfig.Yaml.Converters;

/// <summary>
/// Single source of truth for the YAML 1.1 / PyYAML <c>SafeLoader</c> implicit
/// integer grammar. Cloud-init reads cloud-config via <c>yaml.safe_load</c>,
/// which still uses the YAML 1.1 integer resolver — accepting forms that
/// YamlDotNet's YAML 1.2-only parser rejects or silently mis-reads:
/// leading-zero octal (<c>0644</c> → 420), underscore digit separators
/// (<c>1_000</c> → 1000), and binary (<c>0b101</c> → 5). Both the scalar
/// resolver's typed-int path and its <c>object?</c> path call this method so
/// the two cannot drift.
/// </summary>
/// <remarks>
/// PyYAML SafeLoader's implicit integer regex (YAML 1.1) is:
/// <code>
/// ^(?:[-+]?0b[0-1_]+            # binary
///   |[-+]?0[0-7_]+             # octal (leading-zero form)
///   |[-+]?(?:0|[1-9][0-9_]*)   # decimal
///   |[-+]?0x[0-9a-fA-F_]+      # hexadecimal
///   |[-+]?[1-9][0-9_]*(?::[0-5]?[0-9])+)$  # sexagesimal
/// </code>
/// We implement every branch except the trailing sexagesimal one — see the
/// deliberate carve-out in <see cref="TryParse"/>. We additionally accept the
/// YAML 1.2 <c>0o</c> octal prefix for forward-compatibility; it is harmless
/// because PyYAML's leading-zero form already covers the same values.
/// </remarks>
internal static class Yaml11IntegerTokens
{
    /// <summary>
    /// Returns <c>true</c> and the resolved value when <paramref name="text"/>
    /// is a YAML 1.1 implicit integer. Returns <c>false</c> (so callers fall
    /// through to float / string resolution) for anything else — including
    /// the deliberately-unsupported sexagesimal form.
    /// </summary>
    public static bool TryParse(string text, out long value)
    {
        value = 0;
        if (string.IsNullOrEmpty(text))
            return false;

        // Sexagesimal carve-out. PyYAML technically resolves `12:30` to the
        // base-60 integer 750 and `1:30:00` to 5400. We deliberately do NOT,
        // because cloud-config in the wild never relies on sexagesimal
        // integers, while operators routinely write unquoted times-of-day,
        // ratios, or port:host strings. Silently turning `12:30` into 750 is
        // far more dangerous than the theoretical fidelity loss — a
        // colon-bearing scalar stays a string. Documented as a deliberate
        // divergence in differences-from-cloud-init.md.
        if (text.IndexOf(':') >= 0)
            return false;

        var span = text.AsSpan();

        // Optional sign.
        var negative = false;
        if (span[0] is '+' or '-')
        {
            negative = span[0] == '-';
            span = span[1..];
            if (span.IsEmpty)
                return false;
        }

        // Radix prefixes. Length must be > 2 so there is at least one digit
        // after the prefix (PyYAML requires `[digits]+`).
        if (span.Length > 2 && span[0] == '0')
        {
            switch (span[1])
            {
                case 'x' or 'X':
                    return TryParseRadix(span[2..], 16, negative, out value);
                case 'b' or 'B':
                    return TryParseRadix(span[2..], 2, negative, out value);
                case 'o' or 'O':
                    // YAML 1.2 octal — accepted for forward-compat.
                    return TryParseRadix(span[2..], 8, negative, out value);
            }
        }

        // Leading-zero octal (the YAML 1.1 form and the key fix): a `0`
        // followed by one or more octal digits / underscores, e.g. `0644`,
        // `017`. A bare `0` is decimal zero (handled below).
        if (span.Length > 1 && span[0] == '0')
            return TryParseRadix(span[1..], 8, negative, out value);

        // Bare decimal (including bare `0`).
        return TryParseRadix(span, 10, negative, out value);
    }

    private static bool TryParseRadix(ReadOnlySpan<char> digits, int radix, bool negative, out long value)
    {
        value = 0;
        if (digits.IsEmpty)
            return false;

        // Underscores are digit separators in YAML 1.1. PyYAML's grammar
        // allows them anywhere within the digit run; we strip them and then
        // require the remainder to be all valid digits for the radix. A run
        // that is only underscores (no digits left) is rejected.
        var stripped = digits.IndexOf('_') >= 0
            ? StripUnderscores(digits)
            : digits.ToString();
        if (stripped.Length == 0)
            return false;

        try
        {
            // Convert.ToInt64 with a non-decimal radix does not accept a sign,
            // which is why we strip it up front and re-apply it here. It also
            // rejects any non-digit character for the radix, so an underscore
            // that left a stray separator (already stripped) cannot slip
            // through, and `0b102` / `0xZZ` correctly fail.
            var magnitude = radix == 10
                ? long.Parse(stripped, NumberStyles.None, CultureInfo.InvariantCulture)
                : Convert.ToInt64(stripped, radix);
            value = negative ? -magnitude : magnitude;
            return true;
        }
        catch (FormatException) { return false; }
        catch (OverflowException) { return false; }
        catch (ArgumentException) { return false; }
    }

    private static string StripUnderscores(ReadOnlySpan<char> digits)
    {
        // Reject leading / trailing / doubled underscores to stay close to
        // PyYAML, which requires digits on both sides of every separator.
        if (digits[0] == '_' || digits[^1] == '_')
            return string.Empty;

        var buffer = new char[digits.Length];
        var length = 0;
        var previousWasUnderscore = false;
        foreach (var c in digits)
        {
            if (c == '_')
            {
                if (previousWasUnderscore)
                    return string.Empty;
                previousWasUnderscore = true;
                continue;
            }
            previousWasUnderscore = false;
            buffer[length++] = c;
        }
        return new string(buffer, 0, length);
    }
}
