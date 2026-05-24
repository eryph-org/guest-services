namespace Eryph.GuestServices.CloudConfig;

/// <summary>
/// Marks a record (or class / struct) as a participant in the source-generated
/// deep-merge. The generator emits a <c>MergeXyz</c> helper for every type
/// carrying this attribute.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
public sealed class CloudInitRecordAttribute : Attribute;
