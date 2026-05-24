using Eryph.GuestServices.CloudConfig.Validation;
using LanguageExt;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace Eryph.GuestServices.CloudConfig.Yaml.Converters;

/// <summary>
/// Delegates octal-permission validation to
/// <see cref="WriteFilePermissions.NewValidation"/> in the model library
/// so the YAML and CLI <c>validate</c> paths agree on the rules — no
/// duplicated regex / digit-count logic that could drift.
/// </summary>
internal class WriteFilePermissionsYamlConverter : IYamlTypeConverter
{
    // This converter is explicitly attached via WithAttributeOverride and
    // therefore must not claim any type by default.
    public bool Accepts(Type type) => false;

    public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
    {
        var scalar = parser.Consume<Scalar>();
        var raw = scalar.Value;

        // Single source of truth — the model library's ValidatingNewType
        // does the empty/0x-rejected/octal-digit/length-limit checks AND
        // the Normalize pad-to-4-digits step. We re-shape LanguageExt
        // errors into YamlException so YAML parse errors stay locatable
        // (scalar.Start / scalar.End mark the offending node).
        return WriteFilePermissions.NewValidation(raw).Match<object?>(
            Succ: perms => perms.Value,
            Fail: errs => throw new YamlException(
                scalar.Start, scalar.End,
                $"File permissions '{raw}' are invalid: " + string.Join(" ", errs.Map(e => e.Message))));
    }

    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer) =>
        serializer(value, type);
}
