using Eryph.GuestServices.CloudConfig;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace Eryph.GuestServices.CloudConfig.Yaml.Converters;

internal class RuncmdEntryYamlTypeConverter : IYamlTypeConverter
{
    public bool Accepts(Type type) => type == typeof(RuncmdEntry);

    public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
    {
        if (parser.TryConsume<Scalar>(out var scalar))
        {
            return new RuncmdEntry
            {
                IsShellCommand = true,
                Command = scalar.Value,
            };
        }

        var argv = (IReadOnlyList<string>?)rootDeserializer(typeof(IReadOnlyList<string>));
        return new RuncmdEntry
        {
            IsShellCommand = false,
            Argv = argv,
        };
    }

    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer) =>
        serializer(value, type);
}
