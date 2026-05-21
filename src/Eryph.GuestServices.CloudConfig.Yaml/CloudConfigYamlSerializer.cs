using Eryph.ConfigModel.Yaml;
using Eryph.GuestServices.CloudConfig;
using Eryph.GuestServices.CloudConfig.Yaml.Converters;
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
            .WithEnumNamingConvention(UnderscoredNamingConvention.Instance)
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .WithAttributeOverride<CloudConfig>(
                c => c.SshAuthorizedKeys!,
                new YamlConverterAttribute(typeof(StringListYamlConverter)))
            .WithAttributeOverride<CloudConfig>(
                c => c.Runcmd!,
                new YamlConverterAttribute(typeof(RuncmdListYamlConverter)))
            .WithAttributeOverride<WriteFileConfig>(
                w => w.Permissions!,
                new YamlConverterAttribute(typeof(WriteFilePermissionsYamlConverter)));

        // Build the type inspector first as some of our type converters require it.
        var typeInspector = builder.BuildTypeInspector();

        return builder
            .WithTypeConverter(new UserConfigYamlTypeConverter(typeInspector))
            .WithTypeConverter(new RuncmdEntryYamlTypeConverter())
            .WithTypeConverter(new StringListYamlConverter())
            .WithTypeConverter(new RuncmdListYamlConverter())
            .WithTypeConverter(new WriteFilePermissionsYamlConverter())
            .WithNodeDeserializer(
                new ReadOnlyListNodeDeserializer(),
                s => s.Before<CollectionNodeDeserializer>())
            .Build();
    });

    public static CloudConfig Deserialize(string yaml)
    {
        var stripped = StripCloudConfigHeader(yaml);
        try
        {
            return Deserializer.Value.Deserialize<CloudConfig>(new StringParser(stripped));
        }
        catch (Exception ex)
        {
            throw InvalidConfigExceptionFactory.Create(ex);
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
