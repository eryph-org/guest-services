namespace Eryph.GuestServices.CloudConfig;

/// <summary>
/// Override the source-generated merge behaviour for a property. See
/// <see cref="MergeKind"/> for the available strategies.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class MergeBehaviorAttribute(MergeKind kind) : Attribute
{
    public MergeKind Kind { get; } = kind;

    /// <summary>
    /// For <see cref="MergeKind.KeyedByName"/>: the static merge helper name
    /// (e.g. <c>"MergeUser"</c>) the generator dispatches to per-entry.
    /// </summary>
    public string? KeyedMergeMethod { get; init; }
}
