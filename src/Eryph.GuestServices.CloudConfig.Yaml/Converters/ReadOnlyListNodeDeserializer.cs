using YamlDotNet.Core;
using YamlDotNet.Serialization;

namespace Eryph.GuestServices.CloudConfig.Yaml.Converters;

// YamlDotNet cannot instantiate interface collection types out of the box.
// This node deserializer transparently maps IReadOnlyList<T> / IReadOnlyCollection<T>
// to List<T> so the POCOs can use read-only contracts.
internal class ReadOnlyListNodeDeserializer : INodeDeserializer
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
        if (definition != typeof(IReadOnlyList<>) && definition != typeof(IReadOnlyCollection<>))
        {
            value = null;
            return false;
        }

        var elementType = expectedType.GetGenericArguments()[0];
        var listType = typeof(List<>).MakeGenericType(elementType);

        value = nestedObjectDeserializer(reader, listType);
        return value != null;
    }
}
