using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace Eryph.GuestServices.CloudConfig.Yaml.Converters;

/// <summary>
/// Helpers for emitting and reading string scalars that must survive a
/// PyYAML (YAML 1.1) round trip. cloud-init is the reference reader, so a
/// value like <c>20.11</c>, <c>3</c>, <c>yes</c> or <c>null</c> that is
/// semantically a string must be quoted on write — otherwise PyYAML reads it
/// back as a float / int / bool / null.
/// </summary>
internal static partial class YamlStringScalar
{
    // YAML 1.1 bool + null token set (PyYAML SafeLoader), plus int / float.
    private static readonly HashSet<string> BoolNullTokens = new(StringComparer.Ordinal)
    {
        "y", "Y", "yes", "Yes", "YES", "n", "N", "no", "No", "NO",
        "true", "True", "TRUE", "false", "False", "FALSE",
        "on", "On", "ON", "off", "Off", "OFF",
        "~", "null", "Null", "NULL", "",
    };

    [GeneratedRegex(@"^[-+]?[0-9][0-9_]*$")]
    private static partial Regex IntToken();

    [GeneratedRegex(@"^[-+]?(\.[0-9]+|[0-9][0-9_]*(\.[0-9]*)?)([eE][-+]?[0-9]+)?$")]
    private static partial Regex FloatToken();

    /// <summary>
    /// True when a plain scalar with this value would be read back by PyYAML as
    /// a non-string (bool, null, int, float), so it must be quoted on write.
    /// </summary>
    public static bool LooksNonString(string value) =>
        BoolNullTokens.Contains(value)
        || IntToken().IsMatch(value)
        || FloatToken().IsMatch(value);

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
