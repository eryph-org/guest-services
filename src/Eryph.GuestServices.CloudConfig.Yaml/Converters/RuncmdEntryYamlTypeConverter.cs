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

    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
    {
        // A RuncmdEntry serialises back to one of cloud-init's two runcmd
        // shapes — a shell-command string or an argv sequence — NOT to a
        // mapping of its internal fields. Default serialisation would emit
        // the record's properties, which is invalid runcmd.
        if (value is not RuncmdEntry entry)
        {
            emitter.Emit(new Scalar(string.Empty));
            return;
        }

        if (entry.IsShellCommand)
        {
            emitter.Emit(new Scalar(entry.Command ?? string.Empty));
            return;
        }

        emitter.Emit(new SequenceStart(
            AnchorName.Empty, TagName.Empty, isImplicit: true, SequenceStyle.Flow));
        foreach (var arg in entry.Argv ?? [])
            emitter.Emit(new Scalar(arg));
        emitter.Emit(new SequenceEnd());
    }
}
