using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Eryph.GuestServices.CloudConfig.SourceGen;

/// <summary>
/// Emits <c>CloudConfigMerge</c> and <c>CloudConfigPlatformInventory</c>
/// from the model's <c>[CloudInitRoot]</c> / <c>[CloudInitRecord]</c> /
/// <c>[CloudInitField]</c> / <c>[MergeBehavior]</c> attributes.
/// </summary>
[Generator]
public sealed class CloudConfigGenerator : IIncrementalGenerator
{
    private const string AttributeNamespace = "Eryph.GuestServices.CloudConfig";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Discover every type marked with [CloudInitRecord] or [CloudInitRoot].
        var typeProvider = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (node, _) => node is TypeDeclarationSyntax tds && tds.AttributeLists.Count > 0,
                transform: static (ctx, _) => GetRecordTypeOrNull(ctx))
            .Where(static t => t is not null)
            .Select(static (t, _) => t!);

        var collected = typeProvider.Collect();

        context.RegisterSourceOutput(collected, static (spc, types) => Emit(spc, types));
    }

    private static RecordTypeInfo? GetRecordTypeOrNull(GeneratorSyntaxContext ctx)
    {
        if (ctx.Node is not TypeDeclarationSyntax tds)
            return null;
        if (ctx.SemanticModel.GetDeclaredSymbol(tds) is not INamedTypeSymbol symbol)
            return null;

        var isRecord = false;
        var isRoot = false;
        foreach (var attr in symbol.GetAttributes())
        {
            var name = attr.AttributeClass?.ToDisplayString();
            if (name == $"{AttributeNamespace}.CloudInitRecordAttribute")
                isRecord = true;
            else if (name == $"{AttributeNamespace}.CloudInitRootAttribute")
            {
                isRoot = true;
                isRecord = true;
            }
        }

        if (!isRecord)
            return null;

        var properties = new List<PropertyInfo>();
        foreach (var member in symbol.GetMembers())
        {
            if (member is not IPropertySymbol prop)
                continue;
            if (prop.DeclaredAccessibility != Accessibility.Public)
                continue;
            if (prop.IsStatic || prop.IsIndexer)
                continue;
            // The record's compiler-generated EqualityContract is a public get-only string.
            if (prop.Name == "EqualityContract")
                continue;

            CloudInitPlatformsMask platforms = CloudInitPlatformsMask.All;
            string? yamlName = null;
            string? description = null;
            bool hasFieldAttr = false;
            MergeKindEnum mergeKind = MergeKindEnum.Auto;
            string? keyedMergeMethod = null;

            foreach (var attr in prop.GetAttributes())
            {
                var attrName = attr.AttributeClass?.ToDisplayString();
                if (attrName == $"{AttributeNamespace}.CloudInitFieldAttribute")
                {
                    hasFieldAttr = true;
                    foreach (var named in attr.NamedArguments)
                    {
                        switch (named.Key)
                        {
                            case "Platforms":
                                if (named.Value.Value is int p)
                                    platforms = (CloudInitPlatformsMask)p;
                                break;
                            case "YamlName":
                                yamlName = named.Value.Value as string;
                                break;
                            case "Description":
                                description = named.Value.Value as string;
                                break;
                        }
                    }
                }
                else if (attrName == $"{AttributeNamespace}.MergeBehaviorAttribute")
                {
                    if (attr.ConstructorArguments.Length > 0 && attr.ConstructorArguments[0].Value is int k)
                        mergeKind = (MergeKindEnum)k;
                    foreach (var named in attr.NamedArguments)
                    {
                        if (named.Key == "KeyedMergeMethod")
                            keyedMergeMethod = named.Value.Value as string;
                    }
                }
            }

            properties.Add(new PropertyInfo(
                Name: prop.Name,
                TypeDisplay: prop.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                TypeShortDisplay: prop.Type.ToDisplayString(),
                TypeSymbol: prop.Type,
                HasFieldAttribute: hasFieldAttr,
                Platforms: platforms,
                YamlName: yamlName,
                Description: description,
                MergeKind: mergeKind,
                KeyedMergeMethod: keyedMergeMethod));
        }

        return new RecordTypeInfo(
            Name: symbol.Name,
            FullyQualifiedName: symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            IsRoot: isRoot,
            Properties: properties);
    }

    private static void Emit(SourceProductionContext spc, ImmutableArray<RecordTypeInfo> types)
    {
        if (types.IsDefaultOrEmpty)
            return;

        // The set of all record types known to the generator drives DeepMerge inference.
        var recordNames = new HashSet<string>(types.Select(t => t.FullyQualifiedName));
        var root = types.FirstOrDefault(t => t.IsRoot);

        EmitMerge(spc, types, recordNames, root);

        if (root is not null)
            EmitInventory(spc, root);
    }

    private static void EmitMerge(
        SourceProductionContext spc,
        ImmutableArray<RecordTypeInfo> types,
        HashSet<string> recordNames,
        RecordTypeInfo? root)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine("namespace Eryph.GuestServices.CloudConfig;");
        sb.AppendLine();
        sb.AppendLine("partial class CloudConfigMerge");
        sb.AppendLine("{");

        if (root is not null)
        {
            EmitMergeMethod(sb, root, recordNames, isRootEntryPoint: true);
        }

        foreach (var t in types.Where(t => !t.IsRoot))
        {
            EmitMergeMethod(sb, t, recordNames, isRootEntryPoint: false);
        }

        sb.AppendLine("}");

        spc.AddSource("CloudConfigMerge.Merge.g.cs", SourceText.From(sb.ToString(), Encoding.UTF8));
    }

    private static void EmitMergeMethod(
        StringBuilder sb,
        RecordTypeInfo t,
        HashSet<string> recordNames,
        bool isRootEntryPoint)
    {
        var methodName = isRootEntryPoint ? "Merge" : $"Merge{t.Name}";
        var fq = t.FullyQualifiedName;

        sb.AppendLine();
        // Use the fully qualified name in the doc comment so the cref resolves
        // even when the target record lives in a different namespace from
        // CloudConfigMerge (e.g. records under Eryph.GuestServices.CloudConfig.Linux).
        sb.AppendLine($"    /// <summary>Source-generated deep merge for <see cref=\"{fq.Replace('<', '{').Replace('>', '}')}\"/>.</summary>");

        // Every merge method carries the per-fragment cloud-init merge_how
        // directive (RFC 0032) so list/dict overrides reach the hand-written
        // helpers. The root overload is the strategy-aware entry point; a
        // convenience Merge(left, right) using the default lives in the
        // hand-written partial.
        const string optionsParam =
            "global::Eryph.GuestServices.CloudConfig.CloudInitMergeOptions options";

        if (isRootEntryPoint)
        {
            sb.AppendLine($"    public static {fq} {methodName}({fq} left, {fq} right, {optionsParam})");
        }
        else
        {
            sb.AppendLine($"    private static {fq}? {methodName}({fq}? left, {fq}? right, {optionsParam})");
            sb.AppendLine("    {");
            sb.AppendLine("        if (left is null) return right;");
            sb.AppendLine("        if (right is null) return left;");
            sb.Append("        return new ").Append(fq).Append("()");
        }

        if (isRootEntryPoint)
        {
            sb.AppendLine("    {");
            sb.AppendLine("        if (options is null) throw new global::System.ArgumentNullException(nameof(options));");
            sb.Append("        return new ").Append(fq).Append("()");
        }

        sb.AppendLine();
        sb.AppendLine("        {");

        for (int i = 0; i < t.Properties.Count; i++)
        {
            var p = t.Properties[i];
            var assignment = BuildPropertyMerge(p, recordNames);
            sb.Append("            ").Append(p.Name).Append(" = ").Append(assignment);
            sb.AppendLine(",");
        }

        sb.AppendLine("        };");
        sb.AppendLine("    }");
    }

    private static string BuildPropertyMerge(PropertyInfo p, HashSet<string> recordNames)
    {
        var kind = p.MergeKind;
        if (kind == MergeKindEnum.Auto)
            kind = InferMergeKind(p, recordNames);

        return kind switch
        {
            MergeKindEnum.RightWins => BuildRightWinsExpression(p),
            MergeKindEnum.Concat => $"Concat(left.{p.Name}, right.{p.Name}, options)",
            MergeKindEnum.DeepMerge => BuildDeepMergeCall(p, recordNames),
            MergeKindEnum.KeyedByName => BuildKeyedByNameCall(p),
            MergeKindEnum.DictMerge => BuildDictMergeCall(p, recordNames),
            _ => BuildRightWinsExpression(p),
        };
    }

    private static string BuildRightWinsExpression(PropertyInfo p)
    {
        // Structured primitives — value-type structs with a public `IsEmpty`
        // property — use IsEmpty as the "did the operator set this?" signal.
        // Recognising the shape rather than hard-coding BoolOrString keeps
        // future similar types (e.g. BoolOrIntOrString) plug-and-play.
        if (HasIsEmptyProperty(p.TypeSymbol))
            return $"(right.{p.Name}.IsEmpty ? left.{p.Name} : right.{p.Name})";

        // Non-nullable types (e.g. `required bool`) cannot fall back via `??`
        // — right's value is authoritative. Nullable types (the common case)
        // use the standard `right ?? left` merge.
        if (IsNullable(p.TypeSymbol))
            return $"right.{p.Name} ?? left.{p.Name}";
        return $"right.{p.Name}";
    }

    private static bool HasIsEmptyProperty(ITypeSymbol type)
    {
        // Only value types can be treated as "structured primitives" here —
        // reference types use plain RightWins via `??`.
        if (!type.IsValueType)
            return false;
        // Skip Nullable<T> wrappers (their IsEmpty-like notion is HasValue).
        if (type is INamedTypeSymbol named
            && named.IsGenericType
            && named.OriginalDefinition.ToDisplayString() == "System.Nullable<T>")
            return false;

        foreach (var member in type.GetMembers("IsEmpty"))
        {
            if (member is IPropertySymbol prop
                && prop.DeclaredAccessibility == Accessibility.Public
                && !prop.IsStatic
                && prop.Type.SpecialType == SpecialType.System_Boolean
                && prop.GetMethod is not null)
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsNullable(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol named
            && named.IsGenericType
            && named.OriginalDefinition.ToDisplayString() == "System.Nullable<T>")
            return true;
        // Reference types: nullable when NullableAnnotation == Annotated.
        if (type.IsReferenceType)
            return type.NullableAnnotation == NullableAnnotation.Annotated;
        return false;
    }

    private static MergeKindEnum InferMergeKind(PropertyInfo p, HashSet<string> recordNames)
    {
        // For dict-like properties (declared as IReadOnlyDictionary<K,V>) → DictMerge.
        // Check this BEFORE IsListLike — dictionaries also implement IEnumerable<KVP>.
        if (IsDictLike(p.TypeSymbol))
            return MergeKindEnum.DictMerge;

        // For collection properties (declared as IReadOnlyList<T> or T[]) → Concat.
        if (IsListLike(p.TypeSymbol))
            return MergeKindEnum.Concat;

        // If the underlying type (stripped of nullable annotation) is one of our records → DeepMerge.
        var underlying = StripNullable(p.TypeSymbol);
        if (underlying is not null)
        {
            var fq = underlying.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            if (recordNames.Contains(fq))
                return MergeKindEnum.DeepMerge;
        }

        // Everything else (string?, bool?, int?, enum?, object?) → RightWins.
        return MergeKindEnum.RightWins;
    }

    private static bool IsDictLike(ITypeSymbol type)
    {
        var stripped = StripNullable(type) ?? type;
        if (stripped is INamedTypeSymbol named)
        {
            var def = named.OriginalDefinition.ToDisplayString();
            if (def == "System.Collections.Generic.IReadOnlyDictionary<TKey, TValue>"
                || def == "System.Collections.Generic.IDictionary<TKey, TValue>"
                || def == "System.Collections.Generic.Dictionary<TKey, TValue>")
                return true;
        }
        return false;
    }

    private static bool IsListLike(ITypeSymbol type)
    {
        var stripped = StripNullable(type) ?? type;
        if (stripped is IArrayTypeSymbol)
            return true;
        if (stripped is INamedTypeSymbol named)
        {
            var def = named.OriginalDefinition.ToDisplayString();
            if (def == "System.Collections.Generic.IReadOnlyList<T>"
                || def == "System.Collections.Generic.IReadOnlyCollection<T>"
                || def == "System.Collections.Generic.IList<T>"
                || def == "System.Collections.Generic.List<T>"
                || def == "System.Collections.Generic.IEnumerable<T>")
                return true;
        }
        return false;
    }

    private static ITypeSymbol? StripNullable(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol named
            && named.IsGenericType
            && named.OriginalDefinition.ToDisplayString() == "System.Nullable<T>")
        {
            return named.TypeArguments[0];
        }
        return type;
    }

    private static string BuildDeepMergeCall(PropertyInfo p, HashSet<string> recordNames)
    {
        // Underlying record type name → "Merge{TypeName}" helper.
        var underlying = StripNullable(p.TypeSymbol);
        if (underlying is not INamedTypeSymbol named)
            return $"right.{p.Name} ?? left.{p.Name}";

        var helper = $"Merge{named.Name}";
        return $"{helper}(left.{p.Name}, right.{p.Name}, options)";
    }

    private static string BuildKeyedByNameCall(PropertyInfo p)
    {
        if (string.IsNullOrEmpty(p.KeyedMergeMethod))
            return $"right.{p.Name} ?? left.{p.Name}";

        return $"MergeByName(left.{p.Name}, right.{p.Name}, e => e.Name, {p.KeyedMergeMethod}, options)";
    }

    private static string BuildDictMergeCall(PropertyInfo p, HashSet<string> recordNames)
    {
        // Determine the value type. When the value type itself is a known
        // [CloudInitRecord], pass the per-record merger so right-side entries
        // deep-merge into left-side entries sharing the same key. Otherwise
        // pass null so MergeDict performs straight key-by-key replacement.
        var stripped = StripNullable(p.TypeSymbol) ?? p.TypeSymbol;
        if (stripped is not INamedTypeSymbol named || named.TypeArguments.Length != 2)
            return $"right.{p.Name} ?? left.{p.Name}";

        var valueType = named.TypeArguments[1];
        var valueFq = valueType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        if (recordNames.Contains(valueFq))
        {
            var valueName = ((INamedTypeSymbol)valueType).Name;
            var helper = $"Merge{valueName}";
            return $"MergeDict(left.{p.Name}, right.{p.Name}, {helper}, options)";
        }

        return $"MergeDict(left.{p.Name}, right.{p.Name}, options)";
    }

    private static void EmitInventory(SourceProductionContext spc, RecordTypeInfo root)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine();
        sb.AppendLine("namespace Eryph.GuestServices.CloudConfig;");
        sb.AppendLine();
        sb.AppendLine("/// <summary>");
        sb.AppendLine("/// Source-generated inventory of every <see cref=\"CloudInitFieldAttribute\"/>");
        sb.AppendLine("/// declared on the root cloud-config record. Drives operator-visible logging");
        sb.AppendLine("/// (e.g. \"acknowledged Linux-only key\") from the model itself rather than a");
        sb.AppendLine("/// hand-curated parallel list.");
        sb.AppendLine("/// </summary>");
        sb.AppendLine("public static class CloudConfigPlatformInventory");
        sb.AppendLine("{");
        sb.AppendLine("    /// <summary>One entry per [CloudInitField]-annotated property on the root.</summary>");
        sb.AppendLine("    public sealed record FieldEntry(");
        sb.AppendLine("        string YamlName,");
        sb.AppendLine("        CloudInitPlatforms Platforms,");
        sb.AppendLine("        string Description,");
        sb.AppendLine("        Func<CloudConfig, bool> Present);");
        sb.AppendLine();
        sb.AppendLine("    public static IReadOnlyList<FieldEntry> Fields { get; } = new FieldEntry[]");
        sb.AppendLine("    {");

        foreach (var p in root.Properties.Where(p => p.HasFieldAttribute))
        {
            var yaml = p.YamlName ?? ToSnakeCase(p.Name);
            var platforms = PlatformsLiteral(p.Platforms);
            var desc = p.Description ?? "Linux-only cloud-init key";
            var present = BuildPresenceCheck(p);
            sb.Append("        new FieldEntry(\"")
              .Append(EscapeCSharpString(yaml))
              .Append("\", ")
              .Append(platforms)
              .Append(", \"")
              .Append(EscapeCSharpString(desc))
              .Append("\", c => ")
              .Append(present)
              .AppendLine("),");
        }

        sb.AppendLine("    };");
        sb.AppendLine("}");

        spc.AddSource("CloudConfigPlatformInventory.g.cs", SourceText.From(sb.ToString(), Encoding.UTF8));
    }

    private static string BuildPresenceCheck(PropertyInfo p)
    {
        // bool? / int? / numeric? → HasValue
        if (p.TypeSymbol is INamedTypeSymbol named
            && named.IsGenericType
            && named.OriginalDefinition.ToDisplayString() == "System.Nullable<T>")
        {
            return $"c.{p.Name}.HasValue";
        }

        // string? → !string.IsNullOrEmpty
        if (p.TypeSymbol.SpecialType == SpecialType.System_String)
        {
            return $"!string.IsNullOrEmpty(c.{p.Name})";
        }

        // IReadOnlyList<T>? / IReadOnlyDictionary<K,V>? → !null && Count > 0
        // so "operator wrote nothing" stays silent on the Info channel even
        // when the YAML parser yielded an empty collection.
        if (IsListLike(p.TypeSymbol) || IsDictLike(p.TypeSymbol))
        {
            return $"c.{p.Name} is not null && c.{p.Name}.Count > 0";
        }

        // Structured primitives (BoolOrString and any future value-type
        // struct with a public IsEmpty bool property) → !IsEmpty.
        if (HasIsEmptyProperty(p.TypeSymbol))
        {
            return $"!c.{p.Name}.IsEmpty";
        }

        // Everything else (reference types) → not null
        return $"c.{p.Name} is not null";
    }

    private static string PlatformsLiteral(CloudInitPlatformsMask mask)
    {
        if (mask == CloudInitPlatformsMask.All)
            return "CloudInitPlatforms.All";
        if (mask == CloudInitPlatformsMask.None)
            return "CloudInitPlatforms.None";
        if (mask == CloudInitPlatformsMask.Linux)
            return "CloudInitPlatforms.Linux";
        if (mask == CloudInitPlatformsMask.Windows)
            return "CloudInitPlatforms.Windows";

        var parts = new List<string>();
        if ((mask & CloudInitPlatformsMask.Linux) != 0) parts.Add("CloudInitPlatforms.Linux");
        if ((mask & CloudInitPlatformsMask.Windows) != 0) parts.Add("CloudInitPlatforms.Windows");
        return string.Join(" | ", parts);
    }

    private static string ToSnakeCase(string name)
    {
        // Match YamlDotNet's UnderscoredNamingConvention: split on uppercase
        // boundaries (single-letter acronyms inclusive), lowercase, join with '_'.
        var sb = new StringBuilder(name.Length + 4);
        for (int i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c) && i > 0)
                sb.Append('_');
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    private static string EscapeCSharpString(string s)
    {
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    // ---------------------------------------------------------------------
    // Local model
    // ---------------------------------------------------------------------

    internal enum MergeKindEnum
    {
        Auto = 0,
        RightWins = 1,
        Concat = 2,
        DeepMerge = 3,
        KeyedByName = 4,
        DictMerge = 5,
    }

    [System.Flags]
    internal enum CloudInitPlatformsMask
    {
        None = 0,
        Linux = 1,
        Windows = 2,
        All = Linux | Windows,
    }

    internal sealed record RecordTypeInfo(
        string Name,
        string FullyQualifiedName,
        bool IsRoot,
        List<PropertyInfo> Properties);

    internal sealed record PropertyInfo(
        string Name,
        string TypeDisplay,
        string TypeShortDisplay,
        ITypeSymbol TypeSymbol,
        bool HasFieldAttribute,
        CloudInitPlatformsMask Platforms,
        string? YamlName,
        string? Description,
        MergeKindEnum MergeKind,
        string? KeyedMergeMethod);
}
