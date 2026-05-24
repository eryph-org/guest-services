using Eryph.ConfigModel.Yaml;
using Eryph.GuestServices.CloudConfig;
using Eryph.GuestServices.CloudConfig.Linux;
using Eryph.GuestServices.CloudConfig.Yaml.Converters;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using YamlDotNet.Serialization.NodeDeserializers;

namespace Eryph.GuestServices.CloudConfig.Yaml;

public static class CloudConfigYamlSerializer
{
    private static readonly Lazy<IDeserializer> Deserializer = new(() =>
    {
        var builder = new DeserializerBuilder()
            .WithCaseInsensitivePropertyMatching()
            // Cloud-init's runtime behaviour for unknown cloud-config keys
            // (see cloudinit/config/schema.py `validate_cloudconfig_schema`)
            // is "warn and continue", NOT "fail" and NOT "silent". We mirror
            // that: deserialization is tolerant (this flag), and a separate
            // top-level walker calls the caller-supplied `onUnknownKey`
            // callback so the DI'd `CloudConfigSerializer` can log at
            // Warning. Cross-cloud cloud-config with Linux-only keys
            // (apt, snap, ntp_client, …) parses cleanly; typos like
            // `hsotname:` surface visibly in the cloud-init.log analogue.
            .IgnoreUnmatchedProperties()
            .WithEnumNamingConvention(UnderscoredNamingConvention.Instance)
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .WithAttributeOverride<CloudConfig>(
                c => c.SshAuthorizedKeys!,
                new YamlConverterAttribute(typeof(StringListYamlConverter)))
            .WithAttributeOverride<CloudConfig>(
                c => c.Runcmd!,
                new YamlConverterAttribute(typeof(RuncmdListYamlConverter)))
            .WithAttributeOverride<CloudConfig>(
                c => c.Bootcmd!,
                new YamlConverterAttribute(typeof(RuncmdListYamlConverter)))
            .WithAttributeOverride<CloudConfig>(
                c => c.SshImportId!,
                new YamlConverterAttribute(typeof(StringListYamlConverter)))
            .WithAttributeOverride<PhoneHomeConfig>(
                p => p.Post!,
                new YamlConverterAttribute(typeof(StringListYamlConverter)))
            .WithAttributeOverride<WriteFileConfig>(
                w => w.Permissions!,
                new YamlConverterAttribute(typeof(WriteFilePermissionsYamlConverter)))
            // Rename properties whose snake-cased default name conflicts
            // with a C# rule (e.g. AnsibleConfig.AnsibleConfigPath maps to
            // ansible_config — clashing with the type name) or whose
            // cloud-init spelling diverges from our naming convention.
            .WithAttributeOverride<AnsibleConfig>(
                a => a.AnsibleConfigPath!,
                new YamlMemberAttribute { Alias = "ansible_config", ApplyNamingConventions = false })
            .WithAttributeOverride<CaCertsConfig>(
                c => c.RemoveDefaultsLegacy!,
                new YamlMemberAttribute { Alias = "remove-defaults", ApplyNamingConventions = false })
            // Cloud-init's documented schema concatenates several ssh_*
            // and locale_* keys without underscores between the trailing
            // words; our property names follow Pascal casing so the
            // UnderscoredNamingConvention would emit ssh_delete_keys etc.,
            // which would NOT match the documented YAML keys.
            .WithAttributeOverride<CloudConfig>(
                c => c.SshDeleteKeys!,
                new YamlMemberAttribute { Alias = "ssh_deletekeys", ApplyNamingConventions = false })
            .WithAttributeOverride<CloudConfig>(
                c => c.SshGenKeyTypes!,
                new YamlMemberAttribute { Alias = "ssh_genkeytypes", ApplyNamingConventions = false })
            .WithAttributeOverride<CloudConfig>(
                c => c.SshPublishHostKeys!,
                new YamlMemberAttribute { Alias = "ssh_publish_hostkeys", ApplyNamingConventions = false })
            .WithAttributeOverride<CloudConfig>(
                c => c.LocaleConfigFile!,
                new YamlMemberAttribute { Alias = "locale_configfile", ApplyNamingConventions = false });

        // Build the type inspector first as some of our type converters require it.
        var typeInspector = builder.BuildTypeInspector();

        return builder
            .WithTypeConverter(new UserConfigYamlTypeConverter(typeInspector))
            .WithTypeConverter(new RuncmdEntryYamlTypeConverter())
            // The following converters return Accepts(_) => false because they are
            // attached via WithAttributeOverride above. They must still be registered
            // here so YamlDotNet can resolve them through its TypeConverterCache when
            // the attribute override is applied.
            .WithTypeConverter(new StringListYamlConverter())
            .WithTypeConverter(new RuncmdListYamlConverter())
            .WithTypeConverter(new WriteFilePermissionsYamlConverter())
            // PyYAML-equivalent YAML 1.2 schema resolution for object?
            // targets. Cloud-init relies on it for every bool-or-string
            // union (power_state.condition is the canonical example);
            // installing this at the parser layer means we get the same
            // semantics for every present and future object? field
            // without per-property attribute overrides.
            .WithNodeDeserializer(
                new YamlSchemaTypeResolver(),
                s => s.Before<ScalarNodeDeserializer>())
            .WithNodeDeserializer(
                new ReadOnlyListNodeDeserializer(),
                s => s.Before<CollectionNodeDeserializer>())
            .WithNodeDeserializer(
                new ReadOnlyDictionaryNodeDeserializer(),
                s => s.Before<DictionaryNodeDeserializer>())
            .Build();
    });

    public static CloudConfig Deserialize(string yaml) => Deserialize(yaml, onUnknownKey: null);

    /// <summary>
    /// Deserialize cloud-config YAML. <paramref name="onUnknownKey"/> — when
    /// supplied — is invoked once per top-level key that is not present on
    /// <see cref="CloudConfig"/>. Mirrors cloud-init's runtime validation:
    /// warn-but-continue. The static parser stays logger-free so callers in
    /// any layer (provisioning service, validate CLI, tests) can decide
    /// what to do with the warnings.
    /// </summary>
    public static CloudConfig Deserialize(string yaml, Action<string>? onUnknownKey)
    {
        var stripped = StripCloudConfigHeader(yaml);
        // An empty document (e.g. just the "#cloud-config" header or fully blank input)
        // is valid and represents a CloudConfig with all-null fields. YamlDotNet would
        // otherwise throw on empty input.
        if (string.IsNullOrWhiteSpace(stripped))
            return new CloudConfig();

        if (onUnknownKey is not null)
            WalkForUnknownTopLevelKeys(stripped, onUnknownKey);

        try
        {
            return Deserializer.Value.Deserialize<CloudConfig>(new StringParser(stripped));
        }
        catch (Exception ex)
        {
            throw InvalidConfigExceptionFactory.Create(ex);
        }
    }

    // The set of YAML keys that exist on the CloudConfig schema. Sourced
    // from the source-generated inventory so every [CloudInitField] tag —
    // including the ones that override the default snake-cased name (e.g.
    // ssh_deletekeys vs the property-derived ssh_delete_keys) — flows
    // through here automatically. Built once and cached.
    private static readonly Lazy<HashSet<string>> KnownTopLevelKeys = new(() =>
        CloudConfigPlatformInventory.Fields
            .Select(f => f.YamlName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase));

    private static void WalkForUnknownTopLevelKeys(string yaml, Action<string> onUnknownKey)
    {
        // Parse to YamlDocument so we can inspect the keys without going
        // through the strongly-typed deserializer. A malformed document
        // here is fine to swallow — the real Deserialize call below will
        // produce the canonical error.
        YamlStream stream;
        try
        {
            stream = new YamlStream();
            stream.Load(new StringReader(yaml));
        }
        catch
        {
            return;
        }
        if (stream.Documents.Count == 0) return;
        if (stream.Documents[0].RootNode is not YamlMappingNode root) return;

        var known = KnownTopLevelKeys.Value;
        foreach (var entry in root.Children)
        {
            if (entry.Key is not YamlScalarNode keyNode) continue;
            var keyName = keyNode.Value;
            if (string.IsNullOrEmpty(keyName)) continue;
            if (!known.Contains(keyName))
                onUnknownKey(keyName);
        }
    }

    private static string StripCloudConfigHeader(string yaml)
    {
        if (string.IsNullOrEmpty(yaml))
            return yaml;

        var index = 0;
        while (index < yaml.Length)
        {
            var lineEnd = yaml.IndexOf('\n', index);
            var line = lineEnd < 0 ? yaml[index..] : yaml[index..lineEnd];
            var trimmed = line.TrimEnd('\r').Trim();
            if (trimmed.Length == 0)
            {
                if (lineEnd < 0) return yaml;
                index = lineEnd + 1;
                continue;
            }

            if (trimmed.Equals("#cloud-config", StringComparison.Ordinal))
            {
                if (lineEnd < 0) return string.Empty;
                // Preserve newlines so that error line numbers stay aligned.
                return new string('\n', CountNewlinesUpTo(yaml, lineEnd + 1)) + yaml[(lineEnd + 1)..];
            }

            return yaml;
        }

        return yaml;
    }

    private static int CountNewlinesUpTo(string text, int exclusiveEnd)
    {
        var count = 0;
        for (var i = 0; i < exclusiveEnd; i++)
        {
            if (text[i] == '\n') count++;
        }
        return count;
    }
}
