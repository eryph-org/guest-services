namespace Eryph.GuestServices.CloudConfig;

/// <summary>
/// Marks a property on a CloudConfig record as a cloud-init field. The
/// source generator uses this metadata to emit the platform inventory.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class CloudInitFieldAttribute : Attribute
{
    /// <summary>Platforms this key has meaningful behaviour on. Default <see cref="CloudInitPlatforms.All"/>.</summary>
    public CloudInitPlatforms Platforms { get; init; } = CloudInitPlatforms.All;

    /// <summary>
    /// The snake_case YAML key. When omitted, derived from the property name
    /// via the underscored naming convention used by the YAML deserializer.
    /// </summary>
    public string? YamlName { get; init; }

    /// <summary>
    /// Operator-visible reason text used by inventory-driven logging
    /// (e.g. for Linux-only keys reported as Information). When omitted the
    /// generator falls back to <c>"Linux-only cloud-init key"</c>.
    /// </summary>
    public string? Description { get; init; }
}
