using System.Globalization;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace Eryph.GuestServices.CloudConfig.Yaml.Converters;

/// <summary>
/// PyYAML-equivalent type resolver for <c>object?</c>-targeted scalars.
/// YamlDotNet's default for an <c>object</c> target is "return the raw
/// scalar text as a string", losing the YAML 1.2 schema's implicit type
/// resolution that PyYAML (and therefore cloud-init) relies on. Without
/// this fix, every <c>object?</c> property on <c>CloudConfig</c> silently
/// diverges from cloud-init's behaviour — <c>condition: true</c> arrives
/// as a string, not a bool; <c>manage_etc_hosts: true</c> the same; and
/// so on across every cross-cloud cloud-config field.
/// </summary>
/// <remarks>
/// Cloud-init is the YAML behavioural reference. PyYAML resolves plain
/// (unquoted) scalars via the YAML 1.2 core schema: <c>true</c>/<c>false</c>
/// → bool, <c>null</c>/<c>~</c> → null, integer literals → long, float
/// literals → double, anything else → string. Quoted scalars stay strings
/// regardless of their text. This resolver does the same for every
/// <c>object</c>-typed target on the deserializer chain, so the agent's
/// runtime types match what cloud-init's Python modules would see.
/// </remarks>
internal class YamlSchemaTypeResolver : INodeDeserializer
{
    public bool Deserialize(
        IParser reader,
        Type expectedType,
        Func<IParser, Type, object?> nestedObjectDeserializer,
        out object? value,
        ObjectDeserializer rootDeserializer)
    {
        // Only intervene when the caller asked for `object` (i.e. an
        // untyped union). Typed targets — string, bool?, int?, IReadOnlyList<T>?,
        // explicit POCOs — continue through YamlDotNet's normal chain so
        // their type-specific handling is preserved.
        if (expectedType != typeof(object))
        {
            value = null;
            return false;
        }

        // For non-scalar events (mappings / sequences) fall through to the
        // default object handlers — they produce Dictionary<object,object>
        // and List<object> respectively, which is the right shape for
        // acknowledged-but-no-op Apt / Snap / etc. blocks.
        if (!reader.TryConsume<Scalar>(out var scalar))
        {
            value = null;
            return false;
        }

        // Quoted scalars are ALWAYS strings — the operator quoted them on
        // purpose. PyYAML respects this; we do too. Cloud-config doesn't
        // use explicit YAML tags (!!str true, !!int 42, etc.), so we don't
        // need to disambiguate the explicit-tag case here — it would also
        // require careful handling of YamlDotNet's TagName API which
        // throws on non-specific tags.
        if (scalar.Style != ScalarStyle.Plain)
        {
            value = scalar.Value;
            return true;
        }

        value = ResolvePlainScalar(scalar.Value);
        return true;
    }

    private static object? ResolvePlainScalar(string text)
    {
        // YAML 1.2 core schema null forms.
        if (text.Length == 0
            || text == "~"
            || text is "null" or "Null" or "NULL")
            return null;

        // YAML 1.2 core schema bool tokens.
        if (text is "true" or "True" or "TRUE") return true;
        if (text is "false" or "False" or "FALSE") return false;

        // Integers — decimal, hex (0x...), octal (0o...). YAML 1.2 dropped
        // the YAML 1.1 leading-zero octal; we follow the modern rules.
        if (TryParseInteger(text, out var asLong))
            return asLong;

        // Floats — including ±.inf and .nan per YAML 1.2.
        if (TryParseFloat(text, out var asDouble))
            return asDouble;

        // Default: keep as string.
        return text;
    }

    private static bool TryParseInteger(string text, out long value)
    {
        // 0x... hex
        if (text.Length > 2 && text[0] == '0' && (text[1] == 'x' || text[1] == 'X')
            && long.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value))
            return true;
        // 0o... octal
        if (text.Length > 2 && text[0] == '0' && (text[1] == 'o' || text[1] == 'O'))
        {
            try
            {
                value = Convert.ToInt64(text[2..], 8);
                return true;
            }
            catch (FormatException) { /* fall through */ }
            catch (OverflowException) { /* fall through */ }
        }
        return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryParseFloat(string text, out double value)
    {
        // YAML 1.2 special floats.
        if (text is ".inf" or ".Inf" or ".INF") { value = double.PositiveInfinity; return true; }
        if (text is "-.inf" or "-.Inf" or "-.INF") { value = double.NegativeInfinity; return true; }
        if (text is "+.inf" or "+.Inf" or "+.INF") { value = double.PositiveInfinity; return true; }
        if (text is ".nan" or ".NaN" or ".NAN") { value = double.NaN; return true; }

        // Reject integers that already parsed elsewhere — TryParse(double) is
        // permissive and would convert "42" to 42.0, but YAML 1.2 wants those
        // as ints. The caller already tried integer first; if we're here the
        // text is not a pure integer.
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
               && text.AsSpan().IndexOfAny('.', 'e', 'E') >= 0;
    }
}
