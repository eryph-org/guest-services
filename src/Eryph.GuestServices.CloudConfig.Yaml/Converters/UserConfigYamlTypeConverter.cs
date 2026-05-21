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

            IPropertyDescriptor propertyDescriptor;
            try
            {
                propertyDescriptor = typeInspector.GetProperty(typeof(UserConfig), null, propertyName.Value, false, true);
            }
            catch (SerializationException ex)
            {
                throw new YamlException(propertyName.Start, propertyName.End, ex.Message);
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
        // The type inspector applies UnderscoredNamingConvention, so the descriptor name
        // for SshAuthorizedKeys is "ssh_authorized_keys" and for Groups is "groups".
        descriptorName is "ssh_authorized_keys" or "groups";
}
