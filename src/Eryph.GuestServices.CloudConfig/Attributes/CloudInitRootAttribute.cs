namespace Eryph.GuestServices.CloudConfig;

/// <summary>
/// Marks the top-level cloud-config record. Exactly one type should carry this
/// attribute. The generator emits the root <c>CloudConfigMerge.Merge</c>
/// entry-point and the platform inventory from this type.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class CloudInitRootAttribute : Attribute;
