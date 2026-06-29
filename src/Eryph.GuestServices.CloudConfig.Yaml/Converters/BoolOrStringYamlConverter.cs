using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace Eryph.GuestServices.CloudConfig.Yaml.Converters;

/// <summary>
/// Type converter for <see cref="BoolOrString"/>. Mirrors PyYAML
/// SafeLoader's semantics: a plain scalar matching a YAML 1.1 bool token
/// becomes a bool variant, anything else (including quoted bool tokens
/// and non-bool plain scalars) becomes a string variant. Empty scalars
/// map to <see cref="BoolOrString.Empty"/>.
/// </summary>
/// <remarks>
/// Registering this as an <see cref="IYamlTypeConverter"/> short-circuits
/// the node-deserializer dispatch — the <see cref="Yaml11ScalarResolver"/>'s
/// matching Case-B branch acts as a defensive fallback when the converter
/// is not in play (e.g. constructed parsers in tests). One source of truth
/// for the bool-token table lives in <see cref="Yaml11BoolTokens"/>.
/// </remarks>
internal sealed class BoolOrStringYamlConverter : IYamlTypeConverter
{
    public bool Accepts(Type type) => type == typeof(BoolOrString);

    public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
    {
        var scalar = parser.Consume<Scalar>();

        if (scalar.Value.Length == 0 && scalar.Style == ScalarStyle.Plain)
            return BoolOrString.Empty;

        // Quoted scalars are ALWAYS strings — operator quoted intentionally.
        if (scalar.Style != ScalarStyle.Plain)
            return BoolOrString.FromString(scalar.Value);

        if (Yaml11BoolTokens.TryParse(scalar.Value, out var parsed))
            return BoolOrString.FromBool(parsed);

        return BoolOrString.FromString(scalar.Value);
    }

    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
    {
        var bos = value is BoolOrString b ? b : BoolOrString.Empty;

        if (bos.IsBool)
        {
            // Plain scalar so it resolves back to a YAML 1.1 bool token on read.
            emitter.Emit(new Scalar(bos.Bool!.Value ? "true" : "false"));
            return;
        }

        if (bos.IsString)
        {
            var text = bos.String!;
            // A string variant whose text is a bool token (e.g. "yes") MUST be
            // quoted, otherwise the deserialiser would read it back as a Bool.
            // Empty strings are quoted too so they are not parsed as Empty.
            var style = text.Length == 0 || Yaml11BoolTokens.IsBoolToken(text)
                ? ScalarStyle.SingleQuoted
                : ScalarStyle.Any;
            emitter.Emit(new Scalar(
                AnchorName.Empty, TagName.Empty, text, style,
                isPlainImplicit: true, isQuotedImplicit: true));
            return;
        }

        // Empty: OmitDefaults normally drops the property before we reach here,
        // so this is a defensive fallback emitting an explicit empty scalar.
        emitter.Emit(new Scalar(string.Empty));
    }
}
