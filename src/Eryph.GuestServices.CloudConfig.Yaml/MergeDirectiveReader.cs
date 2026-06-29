using YamlDotNet.RepresentationModel;

namespace Eryph.GuestServices.CloudConfig.Yaml;

/// <summary>
/// Reads cloud-init's <c>merge_how</c> / <c>merge_type</c> directive from a raw
/// cloud-config fragment and resolves it to <see cref="CloudInitMergeOptions"/>
/// (RFC 0032). Both the string form
/// (<c>list(append)+dict(no_replace)+str()</c>) and the structured list form
/// (<c>- name: list / settings: [...]</c>) are supported. The directive is a
/// merge instruction, not config, so it never appears on the typed
/// <see cref="CloudConfig"/> model — it is extracted from the YAML directly.
/// </summary>
public static class MergeDirectiveReader
{
    private const string MergeHowKey = "merge_how";
    private const string MergeTypeKey = "merge_type";

    /// <summary>
    /// Returns the merge options declared by the fragment, or <c>null</c> when
    /// it carries no directive (the caller then uses the cloud-init default).
    /// Malformed YAML yields <c>null</c> — the typed deserializer surfaces the
    /// real parse error.
    /// </summary>
    public static CloudInitMergeOptions? Read(string yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml))
            return null;

        YamlMappingNode? root;
        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(yaml));
            if (stream.Documents.Count == 0)
                return null;
            root = stream.Documents[0].RootNode as YamlMappingNode;
        }
        catch
        {
            return null;
        }

        if (root is null)
            return null;

        var directive = FindValue(root, MergeHowKey) ?? FindValue(root, MergeTypeKey);
        return directive switch
        {
            YamlScalarNode scalar => CloudInitMergeOptions.Parse(scalar.Value),
            YamlSequenceNode sequence => FromStructured(sequence),
            _ => null,
        };
    }

    private static YamlNode? FindValue(YamlMappingNode root, string key)
    {
        foreach (var entry in root.Children)
        {
            if (entry.Key is YamlScalarNode k
                && string.Equals(k.Value, key, StringComparison.OrdinalIgnoreCase))
                return entry.Value;
        }
        return null;
    }

    // Structured form: a sequence of { name: <merger>, settings: [<setting>, ...] }.
    private static CloudInitMergeOptions FromStructured(YamlSequenceNode sequence)
    {
        var options = CloudInitMergeOptions.CloudInitDefault;
        foreach (var node in sequence)
        {
            if (node is not YamlMappingNode merger)
                continue;

            var name = (FindValue(merger, "name") as YamlScalarNode)?.Value;
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var settings = FindValue(merger, "settings") switch
            {
                YamlSequenceNode s => s.Children
                    .OfType<YamlScalarNode>()
                    .Select(n => n.Value ?? string.Empty),
                YamlScalarNode single => [single.Value ?? string.Empty],
                _ => Enumerable.Empty<string>(),
            };

            options = options.WithMerger(name!, settings);
        }
        return options;
    }
}
