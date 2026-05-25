using System.Globalization;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace Eryph.GuestServices.CloudConfig.Yaml.Converters;

/// <summary>
/// PyYAML <c>SafeLoader</c>-equivalent scalar resolver. Cloud-init reads
/// cloud-config via <c>yaml.safe_load</c>, which still uses the YAML 1.1
/// implicit type resolver — recognising 22 bool tokens (<c>true</c>,
/// <c>True</c>, <c>TRUE</c>, <c>false</c>, <c>False</c>, <c>FALSE</c>,
/// <c>yes</c>, <c>Yes</c>, <c>YES</c>, <c>no</c>, <c>No</c>, <c>NO</c>,
/// <c>on</c>, <c>On</c>, <c>ON</c>, <c>off</c>, <c>Off</c>, <c>OFF</c>,
/// <c>y</c>, <c>Y</c>, <c>n</c>, <c>N</c>) instead of YAML 1.2's
/// stripped-down six.
/// </summary>
/// <remarks>
/// <para>
/// Named for the standard (YAML 1.1) rather than the implementor (PyYAML)
/// because we are emulating the standard, not the library — but cloud-
/// init's reliance on PyYAML <c>SafeLoader</c> is what makes that standard
/// the operator-visible contract.
/// </para>
/// <para>
/// The resolver runs <c>Before&lt;ScalarNodeDeserializer&gt;()</c> so it
/// gets a chance to intercept <c>bool</c>/<c>bool?</c>/<c>BoolOrString</c>/
/// integer/<c>object</c> targets before YamlDotNet's built-in (YAML
/// 1.2-only) scalar parser sees them. Other expected types fall through to
/// the remaining pipeline unchanged.
/// </para>
/// <para>
/// Integer parsing follows the YAML 1.1 grammar via
/// <see cref="Yaml11IntegerTokens"/>: leading-zero octal (<c>0644</c> →
/// 420), underscore separators (<c>1_000</c> → 1000), binary, and hex.
/// <c>WriteFilePermissions</c> is NOT affected — it has its own dedicated
/// converter attached via <c>WithAttributeOverride</c>, so the
/// permissions scalar never reaches this resolver as a plain int target.
/// </para>
/// </remarks>
internal sealed class Yaml11ScalarResolver : INodeDeserializer
{
    public bool Deserialize(
        IParser reader,
        Type expectedType,
        Func<IParser, Type, object?> nestedObjectDeserializer,
        out object? value,
        ObjectDeserializer rootDeserializer)
    {
        // bool / bool? — YAML 1.2-only parsing in YamlDotNet's built-in
        // ScalarNodeDeserializer rejects yes/no/on/off/y/n with a
        // YamlException. We intercept and expand to PyYAML SafeLoader's
        // 22 bool tokens. We accept the tokens whether the scalar is
        // plain or quoted: cloud-init's modules treat `package_update: "yes"`
        // and `package_update: yes` the same way (the YAML 1.1 bool tag is
        // applied after parsing for both forms when the target is typed
        // as bool).
        if (expectedType == typeof(bool) || expectedType == typeof(bool?))
        {
            return TryDeserializeBool(reader, expectedType, out value);
        }

        // BoolOrString — cloud-init's documented bool|string union fields
        // (manage_etc_hosts, resize_rootfs, power_state.condition). The
        // operator's quoting intent decides: plain bool token → Bool,
        // anything else (including quoted bool tokens) → String.
        if (expectedType == typeof(BoolOrString))
        {
            return TryDeserializeBoolOrString(reader, out value);
        }

        // int / int? / long / long? — YamlDotNet's built-in parser uses
        // YAML 1.2 integer rules: it would read `0644` as decimal 644 (a
        // SILENT wrong value) and reject `1_000` / `0b101` outright. We
        // intercept and apply PyYAML SafeLoader's YAML 1.1 integer grammar
        // so a leading-zero octal `mtu: 0644` arrives as 420, exactly as
        // cloud-init would see it.
        if (expectedType == typeof(int) || expectedType == typeof(int?)
            || expectedType == typeof(long) || expectedType == typeof(long?))
        {
            return TryDeserializeInteger(reader, expectedType, out value);
        }

        // object? — untyped union targets. Plain scalars get YAML 1.1
        // schema resolution (bool / null / int / float / string); quoted
        // scalars stay as strings (operator quoted them on purpose).
        if (expectedType == typeof(object))
        {
            return TryDeserializeObject(reader, out value);
        }

        // Every other typed target (string, IReadOnlyList<T>, …)
        // continues through YamlDotNet's standard pipeline.
        value = null;
        return false;
    }

    private static bool TryDeserializeBool(IParser reader, Type expectedType, out object? value)
    {
        if (!reader.TryConsume<Scalar>(out var scalar))
        {
            value = null;
            return false;
        }

        // Empty scalar maps to null for bool?; for plain bool it's a
        // legitimate error — cloud-init's PyYAML pipeline would surface
        // a validation error at the same point.
        if (scalar.Value.Length == 0 && scalar.Style == ScalarStyle.Plain)
        {
            if (expectedType == typeof(bool?))
            {
                value = null;
                return true;
            }
            throw new YamlException(
                scalar.Start, scalar.End,
                "Expected a boolean value but found an empty scalar.");
        }

        if (Yaml11BoolTokens.TryParse(scalar.Value, out var parsed))
        {
            value = parsed;
            return true;
        }

        throw new YamlException(
            scalar.Start, scalar.End,
            $"'{scalar.Value}' is not a recognised YAML 1.1 / PyYAML boolean token. "
            + "Accepted forms: true/false, yes/no, on/off, y/n (case-insensitive variants per the YAML 1.1 spec).");
    }

    private static bool TryDeserializeBoolOrString(IParser reader, out object? value)
    {
        if (!reader.TryConsume<Scalar>(out var scalar))
        {
            value = null;
            return false;
        }

        if (scalar.Value.Length == 0 && scalar.Style == ScalarStyle.Plain)
        {
            value = BoolOrString.Empty;
            return true;
        }

        // Quoted (single, double, literal `|`, folded `>`) → string.
        // The operator quoted intentionally, so even `"yes"` stays a string.
        if (scalar.Style != ScalarStyle.Plain)
        {
            value = BoolOrString.FromString(scalar.Value);
            return true;
        }

        if (Yaml11BoolTokens.TryParse(scalar.Value, out var parsed))
        {
            value = BoolOrString.FromBool(parsed);
            return true;
        }

        value = BoolOrString.FromString(scalar.Value);
        return true;
    }

    private static bool TryDeserializeInteger(IParser reader, Type expectedType, out object? value)
    {
        if (!reader.TryConsume<Scalar>(out var scalar))
        {
            value = null;
            return false;
        }

        var isNullable = expectedType == typeof(int?) || expectedType == typeof(long?);

        // Empty scalar maps to null for nullable targets; for a non-nullable
        // int/long it is a locatable error, mirroring the bool case.
        if (scalar.Value.Length == 0 && scalar.Style == ScalarStyle.Plain)
        {
            if (isNullable)
            {
                value = null;
                return true;
            }
            throw new YamlException(
                scalar.Start, scalar.End,
                "Expected an integer value but found an empty scalar.");
        }

        // We accept the integer whether the scalar is plain or quoted: the
        // target type declares the intent (cloud-init's modules coerce
        // `mtu: "1500"` to an int just the same), matching the tolerance
        // rationale of the bool case.
        if (Yaml11IntegerTokens.TryParse(scalar.Value, out var parsed))
        {
            var wantsLong = expectedType == typeof(long) || expectedType == typeof(long?);
            if (wantsLong)
            {
                value = parsed;
                return true;
            }

            // int / int? target — guard the narrowing conversion so an
            // out-of-range value surfaces as a locatable error rather than
            // an OverflowException from deep in the pipeline.
            if (parsed is < int.MinValue or > int.MaxValue)
            {
                throw new YamlException(
                    scalar.Start, scalar.End,
                    $"Integer value '{scalar.Value}' is out of range for a 32-bit field.");
            }
            value = (int)parsed;
            return true;
        }

        throw new YamlException(
            scalar.Start, scalar.End,
            $"'{scalar.Value}' is not a recognised YAML 1.1 integer. "
            + "Accepted forms: decimal, leading-zero octal (0644), 0o/0x/0b prefixes, "
            + "and underscore digit separators (1_000).");
    }

    private static bool TryDeserializeObject(IParser reader, out object? value)
    {
        // For non-scalar events (mappings / sequences) fall through to
        // the default object handlers — they produce Dictionary<object,object>
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
        // need to disambiguate the explicit-tag case here.
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
        // YAML 1.1 null forms (same set as YAML 1.2 for nulls).
        if (text.Length == 0
            || text == "~"
            || text is "null" or "Null" or "NULL")
            return null;

        // YAML 1.1 bool tokens — the cloud-init-relevant expansion.
        if (Yaml11BoolTokens.TryParse(text, out var asBool))
            return asBool;

        // Integers — full YAML 1.1 grammar (decimal, leading-zero octal,
        // 0o/0x/0b prefixes, underscore separators). Shares the single
        // source of truth with the typed-int path so the two cannot drift.
        if (Yaml11IntegerTokens.TryParse(text, out var asLong))
            return asLong;

        // Floats — including ±.inf and .nan.
        if (TryParseFloat(text, out var asDouble))
            return asDouble;

        // Default: keep as string.
        return text;
    }

    private static bool TryParseFloat(string text, out double value)
    {
        // YAML 1.1 / 1.2 special floats.
        if (text is ".inf" or ".Inf" or ".INF") { value = double.PositiveInfinity; return true; }
        if (text is "-.inf" or "-.Inf" or "-.INF") { value = double.NegativeInfinity; return true; }
        if (text is "+.inf" or "+.Inf" or "+.INF") { value = double.PositiveInfinity; return true; }
        if (text is ".nan" or ".NaN" or ".NAN") { value = double.NaN; return true; }

        // YAML 1.1 allows underscore separators in floats too (`1_000.5`).
        // Strip them before parsing, but only when at least one separator is
        // actually present so the common no-underscore path stays alloc-free.
        var candidate = text.IndexOf('_') >= 0 ? text.Replace("_", "") : text;

        // Reject integers that already parsed elsewhere — TryParse(double) is
        // permissive and would convert "42" to 42.0. The caller already tried
        // integer first; if we're here the text is not a pure integer.
        value = 0;
        return double.TryParse(candidate, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
               && candidate.AsSpan().IndexOfAny('.', 'e', 'E') >= 0;
    }
}
