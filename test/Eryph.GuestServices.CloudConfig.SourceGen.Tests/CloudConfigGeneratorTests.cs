using System.Collections.Immutable;
using AwesomeAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Eryph.GuestServices.CloudConfig.SourceGen.Tests;

public sealed class CloudConfigGeneratorTests
{
    // Tiny in-memory compilation covering: attribute declarations, one
    // root with a couple of scalar and nested properties, one [CloudInitRecord].
    // Drives the generator end-to-end so its emitter contract is covered
    // without touching the real model assembly.
    private const string MiniSource = """
        using System;
        using System.Collections.Generic;

        namespace System.Runtime.CompilerServices
        {
            internal static class IsExternalInit { }
        }

        namespace Eryph.GuestServices.CloudConfig
        {
            [Flags]
            public enum CloudInitPlatforms { None = 0, Linux = 1, Windows = 2, All = Linux | Windows }
            public enum MergeKind { Auto, RightWins, Concat, DeepMerge, KeyedByName }

            [AttributeUsage(AttributeTargets.Property)]
            public sealed class CloudInitFieldAttribute : Attribute
            {
                public CloudInitPlatforms Platforms { get; init; } = CloudInitPlatforms.All;
                public string? YamlName { get; init; }
                public string? Description { get; init; }
            }

            [AttributeUsage(AttributeTargets.Property)]
            public sealed class MergeBehaviorAttribute : Attribute
            {
                public MergeBehaviorAttribute(MergeKind kind) { Kind = kind; }
                public MergeKind Kind { get; }
                public string? KeyedMergeMethod { get; init; }
            }

            [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
            public sealed class CloudInitRecordAttribute : Attribute { }

            [AttributeUsage(AttributeTargets.Class)]
            public sealed class CloudInitRootAttribute : Attribute { }

            [CloudInitRecord]
            public sealed record SubConfig
            {
                public string? Name { get; init; }
            }

            // Mimics the BoolOrString primitive — a value-type struct with a
            // public bool IsEmpty property. The generator must recognise the
            // shape generically (not by name) for both presence and merge.
            public readonly record struct UnionPrimitive
            {
                public bool IsEmpty => true;
            }

            [CloudInitRoot]
            [CloudInitRecord]
            public sealed record CloudConfig
            {
                [CloudInitField]
                public string? Hostname { get; init; }

                [CloudInitField(Platforms = CloudInitPlatforms.Linux, Description = "Linux APT")]
                public object? Apt { get; init; }

                [CloudInitField]
                public IReadOnlyList<string>? Keys { get; init; }

                [CloudInitField]
                public SubConfig? Sub { get; init; }

                [CloudInitField]
                public IReadOnlyDictionary<string, string>? StringDict { get; init; }

                [CloudInitField]
                public IReadOnlyDictionary<string, SubConfig>? RecordDict { get; init; }

                [CloudInitField]
                public UnionPrimitive Union { get; init; }
            }

            public static partial class CloudConfigMerge { }
        }
        """;

    [Fact]
    public void Generator_Emits_Merge_And_Inventory_For_Root_And_Records()
    {
        var (driver, diagnostics) = RunGenerator(MiniSource);

        // The mini source intentionally omits the hand-rolled Concat /
        // MergeByName helpers (those live next to the generator output in
        // the real assembly). We only assert the generator itself emitted
        // no diagnostics; downstream compile failures from missing helpers
        // are expected and irrelevant.
        diagnostics.Where(d => d.Id.StartsWith("EGS", StringComparison.Ordinal)).Should().BeEmpty();

        var runResult = driver.GetRunResult();
        var sources = runResult.Results
            .SelectMany(r => r.GeneratedSources)
            .ToList();

        sources.Should().Contain(s => s.HintName == "CloudConfigMerge.Merge.g.cs",
            "the generator must emit the merge file");
        sources.Should().Contain(s => s.HintName == "CloudConfigPlatformInventory.g.cs",
            "the generator must emit the inventory file");

        var mergeText = sources.Single(s => s.HintName == "CloudConfigMerge.Merge.g.cs").SourceText.ToString();
        mergeText.Should().Contain("public static global::Eryph.GuestServices.CloudConfig.CloudConfig Merge(",
            "Merge is the public entry-point for the root type");
        mergeText.Should().Contain("Hostname = right.Hostname ?? left.Hostname",
            "RightWins is the default for nullable scalars");
        mergeText.Should().Contain("Keys = Concat(left.Keys, right.Keys, options)",
            "list-typed properties default to Concat, threading the merge options");
        mergeText.Should().Contain("Sub = MergeSubConfig(left.Sub, right.Sub, options)",
            "nested [CloudInitRecord] properties default to DeepMerge, threading the merge options");
        mergeText.Should().Contain("MergeSubConfig(",
            "the generator must emit a per-record helper");
        mergeText.Should().Contain("StringDict = MergeDict(left.StringDict, right.StringDict, options)",
            "IReadOnlyDictionary with a scalar value type defaults to plain DictMerge");
        mergeText.Should().Contain("RecordDict = MergeDict(left.RecordDict, right.RecordDict, MergeSubConfig, options)",
            "IReadOnlyDictionary with a [CloudInitRecord] value type passes the per-record merger");
        mergeText.Should().Contain("Union = (right.Union.IsEmpty ? left.Union : right.Union)",
            "structured primitives (value-type structs with a public IsEmpty bool) merge via IsEmpty, not ??");

        var inventoryText = sources.Single(s => s.HintName == "CloudConfigPlatformInventory.g.cs").SourceText.ToString();
        inventoryText.Should().Contain("\"hostname\"", "yaml key is snake_cased");
        inventoryText.Should().Contain("\"apt\"");
        inventoryText.Should().Contain("CloudInitPlatforms.Linux",
            "platform flag is carried through");
        inventoryText.Should().Contain("Linux APT",
            "description survives");
        inventoryText.Should().Contain("!c.Union.IsEmpty",
            "structured primitives use !IsEmpty as the presence check");
    }

    [Fact]
    public void Generator_Output_Covers_Every_Root_Property()
    {
        var (driver, _) = RunGenerator(MiniSource);

        var sources = driver.GetRunResult().Results
            .SelectMany(r => r.GeneratedSources)
            .ToList();
        var mergeText = sources.Single(s => s.HintName == "CloudConfigMerge.Merge.g.cs").SourceText.ToString();

        // Every property declared on the root must be assigned in the
        // generated initializer.
        foreach (var name in new[] { "Hostname", "Apt", "Keys", "Sub", "StringDict", "RecordDict", "Union" })
        {
            mergeText.Should().Contain($"{name} = ", $"the generator must cover '{name}'");
        }
    }

    private static (GeneratorDriver Driver, ImmutableArray<Diagnostic> Diagnostics) RunGenerator(string source)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source);
        var references = AppDomain.CurrentDomain
            .GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => MetadataReference.CreateFromFile(a.Location))
            .Cast<MetadataReference>()
            .ToList();

        var compilation = CSharpCompilation.Create(
            assemblyName: "MiniModel",
            syntaxTrees: [syntaxTree],
            references: references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        var generator = new CloudConfigGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out _, out var diagnostics);

        return (driver, diagnostics);
    }
}
