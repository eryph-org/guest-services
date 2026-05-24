namespace Eryph.GuestServices.CloudConfig;

/// <summary>
/// Per-property merge strategy for the source-generated CloudConfig deep-merge.
/// </summary>
public enum MergeKind
{
    /// <summary>
    /// Generator infers from the declared type:
    /// <list type="bullet">
    ///   <item><c>string?</c> / <c>bool?</c> / <c>int?</c> / numeric nullable / enum? / object? → <see cref="RightWins"/></item>
    ///   <item><c>IReadOnlyList&lt;T&gt;?</c> non-keyed → <see cref="Concat"/></item>
    ///   <item>record? carrying <see cref="CloudInitRecordAttribute"/> → <see cref="DeepMerge"/></item>
    /// </list>
    /// </summary>
    Auto,

    /// <summary><c>Name = right.Name ?? left.Name</c>.</summary>
    RightWins,

    /// <summary><c>(left ?? empty).Concat(right ?? empty)</c> preserving null-when-both-null.</summary>
    Concat,

    /// <summary>Recursive nested merge. Both null → null; one null → other; otherwise nested merge.</summary>
    DeepMerge,

    /// <summary>
    /// Keyed-by-name merge of an <c>IReadOnlyList&lt;T&gt;</c>. The user provides a static
    /// <c>T Merge(T, T)</c> helper named by <see cref="MergeBehaviorAttribute.KeyedMergeMethod"/>;
    /// the generator dispatches by <c>Name</c>.
    /// </summary>
    KeyedByName,
}
