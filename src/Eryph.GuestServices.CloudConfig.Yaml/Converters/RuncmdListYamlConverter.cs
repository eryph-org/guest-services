using Eryph.GuestServices.CloudConfig;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace Eryph.GuestServices.CloudConfig.Yaml.Converters;

internal class RuncmdListYamlConverter : IYamlTypeConverter
{
    // This converter is explicitly attached via WithAttributeOverride and
    // therefore must not claim any type by default.
    public bool Accepts(Type type) => false;

    public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
    {
        if (parser.TryConsume<Scalar>(out var scalar))
        {
            return new List<RuncmdEntry>
            {
                new() { IsShellCommand = true, Command = scalar.Value },
            };
        }

        return rootDeserializer(typeof(List<RuncmdEntry>));
    }

    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer) =>
        serializer(value, type);
}
