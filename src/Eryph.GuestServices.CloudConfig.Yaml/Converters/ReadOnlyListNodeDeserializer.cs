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

        // A null result here means the YAML had an explicit null (e.g. "users: ~" or
        // "users:" with no value). Pass that through as a null property value rather
        // than returning false, which would let YamlDotNet fall through and fail with
        // an unintelligible error.
        value = nestedObjectDeserializer(reader, listType);
        return true;
    }
}
