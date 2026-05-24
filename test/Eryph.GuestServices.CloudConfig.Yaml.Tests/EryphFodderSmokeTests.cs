using System.Text.RegularExpressions;
using AwesomeAssertions;
using Eryph.ConfigModel;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Eryph.GuestServices.CloudConfig.Yaml.Tests;

public partial class EryphFodderSmokeTests
{
    private const string GenesRootEnvVar = "ERYPH_GENES_ROOT";
    private const string DefaultGenesRoot = @"S:\eryph\eryph-genes\src";

    // Filesystem integration test: reads cloud-config fodder from the eryph
    // genepool on disk. The genepool is not present on CI build agents, so when
    // no genes (or no cloud-config fodder within them) are found, the test
    // SKIPS rather than failing — a missing genepool is "nothing to check
    // here", not a defect. When genes ARE present every cloud-config snippet
    // must parse without an InvalidConfigException.
    [SkippableFact]
    public void Parse_AllCloudConfigFodderInGenes_Succeeds()
    {
        var root = ResolveGenesRoot();
        Skip.If(
            root is null,
            $"No eryph genes found ('{GenesRootEnvVar}' unset and '{DefaultGenesRoot}' absent); " +
            "skipping the fodder smoke test on this machine.");

        var snippets = CloudConfigFodderSnippets(root!).ToList();
        Skip.If(
            snippets.Count == 0,
            $"Eryph genes root '{root}' contains no cloud-config fodder; nothing to smoke-test.");

        foreach (var snippet in snippets)
        {
            var action = () => CloudConfigYamlSerializer.Deserialize(snippet.CloudConfigYaml);
            action.Should().NotThrow<InvalidConfigException>(
                $"fodder '{snippet.FodderName}' in {snippet.SourcePath} must parse");
        }
    }

    private static string? ResolveGenesRoot()
    {
        var root = Environment.GetEnvironmentVariable(GenesRootEnvVar)
                   ?? (Directory.Exists(DefaultGenesRoot) ? DefaultGenesRoot : null);

        return root is not null && Directory.Exists(root) ? root : null;
    }

    private static IEnumerable<FodderSnippet> CloudConfigFodderSnippets(string root)
    {
        foreach (var file in Directory.EnumerateFiles(root, "*.yaml", SearchOption.AllDirectories))
        {
            foreach (var snippet in ExtractCloudConfigSnippets(file))
                yield return snippet;
        }
    }

    private static IEnumerable<FodderSnippet> ExtractCloudConfigSnippets(string filePath)
    {
        string source;
        try
        {
            source = File.ReadAllText(filePath);
        }
        catch (IOException)
        {
            yield break;
        }

        // Eryph fodder files use {{ variable }} templating which eryph-zero substitutes
        // before reaching the guest. Replace placeholders type-aware: boolean fields get
        // `false`, everything else gets a bare string token.
        var substituted = BooleanFieldPlaceholderRegex().Replace(source, "$1false");
        substituted = PlaceholderRegex().Replace(substituted, "placeholder");

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        OuterFodderFile? parsed;
        try
        {
            parsed = deserializer.Deserialize<OuterFodderFile>(substituted);
        }
        catch
        {
            yield break;
        }

        if (parsed?.Fodder is null)
            yield break;

        var serializer = new SerializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build();

        foreach (var fodder in parsed.Fodder)
        {
            if (!string.Equals(fodder.Type, "cloud-config", StringComparison.OrdinalIgnoreCase))
                continue;

            var contentYaml = fodder.Content switch
            {
                string s => s,
                IDictionary<object, object> m => serializer.Serialize(m),
                IList<object> l => serializer.Serialize(l),
                _ => null,
            };

            if (string.IsNullOrWhiteSpace(contentYaml))
                continue;

            yield return new FodderSnippet
            {
                SourcePath = filePath,
                FodderName = fodder.Name ?? "<unnamed>",
                CloudConfigYaml = contentYaml,
            };
        }
    }

    [GeneratedRegex(@"\{\{[^}]*\}\}")]
    private static partial Regex PlaceholderRegex();

    [GeneratedRegex(@"(\b(?:lock_passwd|expire|inactive|system|defer|append|ssh_pwauth|preserve_hostname|secret|required)\s*:\s*)\{\{[^}]*\}\}")]
    private static partial Regex BooleanFieldPlaceholderRegex();

    public sealed record FodderSnippet
    {
        public required string SourcePath { get; init; }
        public required string FodderName { get; init; }
        public required string CloudConfigYaml { get; init; }

        public override string ToString() => $"{FodderName} ({Path.GetFileName(SourcePath)})";
    }

    private sealed class OuterFodderFile
    {
        public List<OuterFodder>? Fodder { get; set; }
    }

    private sealed class OuterFodder
    {
        public string? Name { get; set; }
        public string? Type { get; set; }
        public object? Content { get; set; }
    }
}

