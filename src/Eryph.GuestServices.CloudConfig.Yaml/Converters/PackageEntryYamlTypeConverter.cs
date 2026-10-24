using Eryph.GuestServices.CloudConfig;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace Eryph.GuestServices.CloudConfig.Yaml.Converters;

/// <summary>
/// An entry of the cloud-init <c>packages:</c> list is one of:
/// a bare scalar (<c>- git</c>), a <c>[name, version]</c> pair, or a
/// manager-keyed mapping (<c>- {winget: [...], chocolatey: [...]}</c>). Mirrors
/// cloud-init's union; eryph adds the <c>winget</c>/<c>chocolatey</c> managers.
/// </summary>
internal sealed class PackageEntryYamlTypeConverter : IYamlTypeConverter
{
    public bool Accepts(Type type) => type == typeof(PackageEntry);

    public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
    {
        // Bare name. A null-like scalar (- ~ / - null / empty) carries no name.
        if (parser.TryConsume<Scalar>(out var scalar))
            return new PackageEntry { Name = YamlStringScalar.IsNullLike(scalar) ? null : scalar.Value };

        // [name, version] pair.
        if (parser.Accept<SequenceStart>(out _))
        {
            var pair = (IReadOnlyList<string>?)rootDeserializer(typeof(List<string>)) ?? [];
            return new PackageEntry
            {
                Name = pair.Count > 0 ? pair[0] : "",
                Version = pair.Count > 1 ? pair[1] : null,
            };
        }

        // Manager-keyed mapping: { <manager>: [ <ManagerPackage>, ... ], ... }.
        parser.Consume<MappingStart>();
        var managers = new Dictionary<string, List<ManagerPackage>>(StringComparer.OrdinalIgnoreCase);
        while (!parser.TryConsume<MappingEnd>(out _))
        {
            var manager = parser.Consume<Scalar>().Value;

            List<ManagerPackage> items;
            if (parser.Accept<Scalar>(out _) || parser.Accept<MappingStart>(out _))
            {
                // Tolerate a single item where a sequence is expected: a scalar
                // (`winget: git`), a null value (`apt:`), or a single mapping
                // (`winget: {id: x}`) — one item, or none.
                var one = (ManagerPackage?)rootDeserializer(typeof(ManagerPackage));
                items = one is null || string.IsNullOrEmpty(one.Name) ? [] : [one];
            }
            else
            {
                items = (List<ManagerPackage>?)rootDeserializer(typeof(List<ManagerPackage>)) ?? [];
            }

            // Duplicate / differing-case manager keys merge rather than clobber.
            if (managers.TryGetValue(manager, out var existing))
                existing.AddRange(items);
            else
                managers[manager] = items;
        }

        return new PackageEntry
        {
            Managers = managers.ToDictionary(
                kv => kv.Key, kv => (IReadOnlyList<ManagerPackage>)kv.Value, StringComparer.OrdinalIgnoreCase),
        };
    }

    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value is not PackageEntry entry)
            throw new InvalidOperationException(
                $"Expected a {nameof(PackageEntry)} to serialise, got {value.GetType().Name}.");

        if (entry.Managers is { Count: > 0 })
        {
            emitter.Emit(new MappingStart(AnchorName.Empty, TagName.Empty, isImplicit: true, MappingStyle.Block));
            foreach (var (manager, items) in entry.Managers)
            {
                emitter.Emit(new Scalar(manager));
                emitter.Emit(new SequenceStart(AnchorName.Empty, TagName.Empty, isImplicit: true, SequenceStyle.Block));
                foreach (var item in items)
                    serializer(item, typeof(ManagerPackage));
                emitter.Emit(new SequenceEnd());
            }
            emitter.Emit(new MappingEnd());
            return;
        }

        // name + version -> [name, version]; bare name -> scalar. Quote values a
        // plain scalar would misread under PyYAML (e.g. a numeric version).
        if (!string.IsNullOrEmpty(entry.Version))
        {
            emitter.Emit(new SequenceStart(AnchorName.Empty, TagName.Empty, isImplicit: true, SequenceStyle.Flow));
            YamlStringScalar.Emit(emitter, entry.Name ?? string.Empty);
            YamlStringScalar.Emit(emitter, entry.Version);
            emitter.Emit(new SequenceEnd());
            return;
        }

        YamlStringScalar.Emit(emitter, entry.Name ?? string.Empty);
    }
}
