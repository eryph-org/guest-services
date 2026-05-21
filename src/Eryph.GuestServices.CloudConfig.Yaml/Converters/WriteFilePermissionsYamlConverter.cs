using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace Eryph.GuestServices.CloudConfig.Yaml.Converters;

internal class WriteFilePermissionsYamlConverter : IYamlTypeConverter
{
    // This converter is explicitly attached via WithAttributeOverride and
    // therefore must not claim any type by default.
    public bool Accepts(Type type) => false;

    public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
    {
        var scalar = parser.Consume<Scalar>();
        var raw = scalar.Value;

        if (string.IsNullOrEmpty(raw))
            throw new YamlException(
                scalar.Start, scalar.End,
                "File permissions must not be empty.");

        if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            throw new YamlException(
                scalar.Start, scalar.End,
                "Hex prefix is not valid for octal file permissions");

        var digits = raw;
        if (digits.StartsWith("0o", StringComparison.OrdinalIgnoreCase))
            digits = digits[2..];

        foreach (var c in digits)
        {
            if (c < '0' || c > '7')
                throw new YamlException(
                    scalar.Start, scalar.End,
                    $"File permissions '{raw}' must be octal digits (0-7).");
        }

        if (digits.Length == 0)
            throw new YamlException(
                scalar.Start, scalar.End,
                $"File permissions '{raw}' must not be empty.");

        if (digits.Length > 4)
            throw new YamlException(
                scalar.Start, scalar.End,
                $"File permissions '{raw}' must not exceed four octal digits.");

        return digits.PadLeft(4, '0');
    }

    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer) =>
        serializer(value, type);
}
