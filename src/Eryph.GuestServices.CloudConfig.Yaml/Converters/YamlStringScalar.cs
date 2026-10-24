using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace Eryph.GuestServices.CloudConfig.Yaml.Converters;

/// <summary>
/// Helpers for emitting and reading string scalars that must survive a
/// PyYAML (YAML 1.1) round trip. cloud-init is the reference reader, so a
/// value like <c>20.11</c>, <c>3</c>, <c>yes</c>, <c>0x1F</c>, <c>2024-01-15</c>
/// or <c>null</c> that is semantically a string must be quoted on write —
/// otherwise PyYAML reads it back as a float / int / bool / date / null.
/// </summary>
internal static class YamlStringScalar
{
    // YAML 1.1 bool + null tokens (PyYAML SafeLoader). These start with a letter,
    // so the letter-leading rule below would otherwise leave them unquoted.
    private static readonly HashSet<string> BoolNullTokens = new(StringComparer.Ordinal)
    {
        "y", "Y", "yes", "Yes", "YES", "n", "N", "no", "No", "NO",
        "true", "True", "TRUE", "false", "False", "FALSE",
        "on", "On", "ON", "off", "Off", "OFF",
        "null", "Null", "NULL",
    };

    /// <summary>
    /// True when a plain scalar with this value would be read back by PyYAML as
    /// a non-string, so it must be quoted on write. Conservative: anything that
    /// does not start with an ASCII letter or <c>_</c> is quoted (covers ints,
    /// floats, hex/octal/binary, sexagesimal, dates/times, <c>.inf</c>/<c>.nan</c>,
    /// <c>~</c> and the empty string), plus the letter-leading bool/null tokens.
    /// Over-quoting a genuine string is harmless; under-quoting is the bug.
    /// </summary>
    public static bool LooksNonString(string value)
    {
        if (value.Length == 0)
            return true;
        if (BoolNullTokens.Contains(value))
            return true;
        var c = value[0];
        var startsStringLike = c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or '_';
        return !startsStringLike;
    }

    /// <summary>Emits a string, quoting it when a plain scalar would be misread.</summary>
    public static void Emit(IEmitter emitter, string? value)
    {
        var text = value ?? string.Empty;
        var style = LooksNonString(text) ? ScalarStyle.DoubleQuoted : ScalarStyle.Any;
        emitter.Emit(new Scalar(AnchorName.Empty, TagName.Empty, text, style, isPlainImplicit: true, isQuotedImplicit: true));
    }

    /// <summary>
    /// A plain scalar whose value is a YAML 1.1 null token (<c>~</c>, <c>null</c>,
    /// empty) read as "no value". Used so <c>- ~</c> / <c>- null</c> / an empty
    /// item does not become a literal package name.
    /// </summary>
    public static bool IsNullLike(Scalar scalar) =>
        scalar.Style is ScalarStyle.Plain or ScalarStyle.Any
        && scalar.Value is "" or "~" or "null" or "Null" or "NULL";
}
