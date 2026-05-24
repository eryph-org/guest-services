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
        // The agent reads cloud-config but does not currently emit it. If
        // a future serialise path needs to round-trip BoolOrString it must
        // decide how to express the empty case (skip the key entirely?
        // emit `~`?) — leaving as NotImplementedException so the gap is
        // obvious if someone wires up a serialise path before the design
        // question is answered.
        throw new NotImplementedException(
            "Serialising BoolOrString is not implemented — the agent only reads cloud-config.");
    }
}
