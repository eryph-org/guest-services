using YamlDotNet.Core;
using YamlDotNet.Serialization;

namespace Eryph.GuestServices.CloudConfig.Yaml.Converters;

// YamlDotNet cannot instantiate interface dictionary types out of the box.
// This node deserializer transparently maps IReadOnlyDictionary<K, V> to
// Dictionary<K, V> so the POCOs can use read-only contracts — matches the
// cloud-init dict-merge semantics for apt.sources, yum_repos, puppet.conf,
// etc., where the dict-shape is the natural cloud-init schema.
internal class ReadOnlyDictionaryNodeDeserializer : INodeDeserializer
{
    public bool Deserialize(
        IParser reader,
        Type expectedType,
        Func<IParser, Type, object?> nestedObjectDeserializer,
        out object? value,
        ObjectDeserializer rootDeserializer)
    {
        if (!expectedType.IsGenericType)
        {
            value = null;
            return false;
        }

        var definition = expectedType.GetGenericTypeDefinition();
        if (definition != typeof(IReadOnlyDictionary<,>))
        {
            value = null;
            return false;
        }

        var args = expectedType.GetGenericArguments();
        var dictType = typeof(Dictionary<,>).MakeGenericType(args);

        // Explicit-null YAML (e.g. "sources: ~") is passed through as a null
        // property value, mirroring the list deserializer's behaviour.
        value = nestedObjectDeserializer(reader, dictType);
        return true;
    }
}
