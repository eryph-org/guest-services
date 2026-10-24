using Eryph.GuestServices.CloudConfig;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace Eryph.GuestServices.CloudConfig.Yaml.Converters;

/// <summary>
/// A package inside a <c>packages:</c> manager group is either a bare scalar
/// (<c>- git</c>) or a small mapping (<c>{id: Git.Git, version: 2.46.0}</c> /
/// <c>{name: nodejs, version: 20.11.0}</c>). Only <c>name</c>/<c>id</c> and
/// <c>version</c> are read; other keys (and non-scalar values) are ignored —
/// use the dedicated <c>winget:</c>/<c>chocolatey:</c> blocks for
/// source/scope/params.
/// </summary>
internal sealed class ManagerPackageYamlTypeConverter : IYamlTypeConverter
{
    public bool Accepts(Type type) => type == typeof(ManagerPackage);

    public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
    {
        if (parser.TryConsume<Scalar>(out var scalar))
            return new ManagerPackage { Name = YamlStringScalar.IsNullLike(scalar) ? "" : scalar.Value };

        parser.Consume<MappingStart>();
        string? name = null;
        string? version = null;
        while (!parser.TryConsume<MappingEnd>(out _))
        {
            var key = parser.Consume<Scalar>().Value;
            // Only scalar values are meaningful here; skip anything else
            // (e.g. a stray list/map value) so one odd key can't fail the doc.
            if (!parser.TryConsume<Scalar>(out var valueScalar))
            {
                parser.SkipThisAndNestedEvents();
                continue;
            }

            switch (key.ToLowerInvariant())
            {
                case "name":
                case "id":
                    name = valueScalar.Value;
                    break;
                case "version":
                    version = valueScalar.Value;
                    break;
                // Other keys are intentionally ignored in the umbrella.
            }
        }

        return new ManagerPackage { Name = name ?? "", Version = version };
    }

    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value is not ManagerPackage pkg)
            throw new InvalidOperationException(
                $"Expected a {nameof(ManagerPackage)} to serialise, got {value.GetType().Name}.");

        // Bare name -> scalar; name + version -> a compact mapping. Quote values
        // that a plain scalar would misread under PyYAML (e.g. a numeric version).
        if (string.IsNullOrEmpty(pkg.Version))
        {
            YamlStringScalar.Emit(emitter, pkg.Name);
            return;
        }

        emitter.Emit(new MappingStart(AnchorName.Empty, TagName.Empty, isImplicit: true, MappingStyle.Flow));
        emitter.Emit(new Scalar("name"));
        YamlStringScalar.Emit(emitter, pkg.Name);
        emitter.Emit(new Scalar("version"));
        YamlStringScalar.Emit(emitter, pkg.Version);
        emitter.Emit(new MappingEnd());
    }
}
