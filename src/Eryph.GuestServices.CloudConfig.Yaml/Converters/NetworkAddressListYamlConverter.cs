using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace Eryph.GuestServices.CloudConfig.Yaml.Converters;

/// <summary>
/// Parses a network-config v2 <c>addresses:</c> list. cloud-init / netplan
/// allow each entry to be either a plain <c>"ip/prefix"</c> scalar or the
/// advanced single-key map form (<c>"ip/prefix": {lifetime: .., label: ..}</c>);
/// we keep the address (the map key) and drop the per-address options the
/// Windows applier does not honour.
/// </summary>
/// <remarks>
/// Critically this must never throw on a structurally valid document. The
/// default <c>List&lt;string&gt;</c> deserializer raises on the map form, and
/// that exception — swallowed upstream — is exactly the failure mode that hid
/// issue #59 (one unexpected sub-shape nulling the entire network-config).
/// Unknown values are drained via <c>rootDeserializer(typeof(object))</c>, the
/// same resilient idiom <c>UserConfigYamlTypeConverter</c> uses.
/// </remarks>
internal sealed class NetworkAddressListYamlConverter : IYamlTypeConverter
{
    // Attached via WithAttributeOverride; must not claim any type by default.
    public bool Accepts(Type type) => false;

    public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
    {
        var result = new List<string>();

        // A lone scalar (or an explicit null) where a list was expected: be
        // tolerant — treat it as a single address, or nothing.
        if (parser.TryConsume<Scalar>(out var lone))
        {
            if (!string.IsNullOrWhiteSpace(lone.Value))
                result.Add(lone.Value);
            return result;
        }

        if (!parser.TryConsume<SequenceStart>(out _))
        {
            // Unexpected shape (e.g. a bare mapping): drain it rather than throw.
            _ = rootDeserializer(typeof(object));
            return result;
        }

        while (!parser.TryConsume<SequenceEnd>(out _))
        {
            if (parser.TryConsume<Scalar>(out var item))
            {
                if (!string.IsNullOrWhiteSpace(item.Value))
                    result.Add(item.Value);
            }
            else if (parser.TryConsume<MappingStart>(out _))
            {
                // Advanced form: { "10.0.0.1/24": {lifetime: 0, label: ..} }.
                // The first key is the address; the value(s) are options we
                // don't apply on Windows, so drain them.
                var first = true;
                while (!parser.TryConsume<MappingEnd>(out _))
                {
                    var key = parser.Consume<Scalar>();
                    if (first && !string.IsNullOrWhiteSpace(key.Value))
                        result.Add(key.Value);
                    first = false;
                    _ = rootDeserializer(typeof(object));
                }
            }
            else
            {
                // Nested sequence or other unexpected node: drain and move on.
                _ = rootDeserializer(typeof(object));
            }
        }

        return result;
    }

    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer) =>
        serializer(value, type);
}
