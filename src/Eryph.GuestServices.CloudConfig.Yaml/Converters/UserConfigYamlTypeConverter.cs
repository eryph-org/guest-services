using System.Runtime.Serialization;
using Eryph.GuestServices.CloudConfig;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace Eryph.GuestServices.CloudConfig.Yaml.Converters;

internal class UserConfigYamlTypeConverter(
    ITypeInspector typeInspector)
    : IYamlTypeConverter
{
    private readonly StringListYamlConverter _stringListConverter = new();

    public bool Accepts(Type type) => type == typeof(UserConfig);

    public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
    {
        if (parser.TryConsume<Scalar>(out var scalar))
        {
            return new UserConfig
            {
                Name = scalar.Value,
            };
        }

        parser.Consume<MappingStart>();

        var result = new UserConfig();

        while (!parser.TryConsume<MappingEnd>(out _))
        {
            var propertyName = parser.Consume<Scalar>();

            IPropertyDescriptor? propertyDescriptor;
            try
            {
                // ignoreUnmatched: true mirrors the root-level deserializer's
                // IgnoreUnmatchedProperties policy — cloud-init's runtime
                // behaviour for unknown user keys is "warn and continue",
                // not "fail". Unknown properties yield a null descriptor
                // here; we drain the value via rootDeserializer(object) so
                // the parser advances past the entire (scalar / mapping /
                // sequence) node without raising.
                propertyDescriptor = typeInspector.GetProperty(typeof(UserConfig), null, propertyName.Value, true, true);
            }
            catch (SerializationException ex)
            {
                throw new YamlException(propertyName.Start, propertyName.End, ex.Message);
            }

            if (propertyDescriptor is null)
            {
                // Unknown user-level property. Drain the value but keep parsing.
                _ = rootDeserializer(typeof(object));
                continue;
            }

            object? propertyValue;
            if (IsStringListShorthandProperty(propertyDescriptor.Name))
            {
                propertyValue = _stringListConverter.ReadYaml(parser, propertyDescriptor.Type, rootDeserializer);
            }
            else
            {
                propertyValue = rootDeserializer(propertyDescriptor.Type);
            }

            propertyDescriptor.Write(result, propertyValue);
        }

        return result;
    }

    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer) =>
        serializer(value, type);

    // keep in sync with UserConfig string-list properties
    private static bool IsStringListShorthandProperty(string descriptorName) =>
        // The type inspector applies UnderscoredNamingConvention, so the descriptor
        // name for SshAuthorizedKeys is "ssh_authorized_keys", for Groups it is
        // "groups", for SshImportId it is "ssh_import_id" and for Sudo it is "sudo".
        // All four accept either a single scalar (promoted to a one-element list)
        // or a sequence of strings — the cloud-init schema documents both forms.
        descriptorName is "ssh_authorized_keys" or "groups" or "sudo" or "ssh_import_id";
}
