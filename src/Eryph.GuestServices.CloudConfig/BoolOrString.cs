namespace Eryph.GuestServices.CloudConfig;

/// <summary>
/// YAML scalar that is either a bool (per PyYAML SafeLoader / YAML 1.1)
/// or a string. Mirrors cloud-init's bool|string union fields —
/// <c>manage_etc_hosts</c>, <c>resize_rootfs</c>, <c>power_state.condition</c>.
/// </summary>
/// <remarks>
/// <para>
/// The operator's quoting intent decides the resolved variant:
/// </para>
/// <list type="bullet">
///   <item>A plain (unquoted) scalar whose text is one of the 22 YAML 1.1
///   bool tokens becomes a <see cref="Bool"/>.</item>
///   <item>A quoted scalar — single, double, or block style — becomes a
///   <see cref="String"/> verbatim, even when its text is a bool token.</item>
///   <item>A plain scalar whose text is not a bool token becomes a
///   <see cref="String"/>.</item>
///   <item>An empty / omitted scalar becomes <see cref="Empty"/>.</item>
/// </list>
/// <para>
/// Value-type struct so the default-constructed form is <see cref="Empty"/>
/// and merge semantics can be derived without a sentinel reference.
/// </para>
/// </remarks>
public readonly record struct BoolOrString
{
    /// <summary>Resolved bool value when the scalar was a plain bool token.</summary>
    public bool? Bool { get; }

    /// <summary>Resolved string value when the scalar was quoted or a non-bool plain token.</summary>
    public string? String { get; }

    private BoolOrString(bool? boolean, string? str)
    {
        Bool = boolean;
        String = str;
    }

    /// <summary>Construct a bool-valued instance.</summary>
    public static BoolOrString FromBool(bool value) => new(value, null);

    /// <summary>Construct a string-valued instance.</summary>
    public static BoolOrString FromString(string value) => new(null, value);

    /// <summary>The empty (no-value) instance. Same as <c>default</c>.</summary>
    public static readonly BoolOrString Empty = default;

    /// <summary><c>true</c> when this instance carries a bool value.</summary>
    public bool IsBool => Bool.HasValue;

    /// <summary><c>true</c> when this instance carries a string value.</summary>
    public bool IsString => String is not null;

    /// <summary><c>true</c> when no value was supplied.</summary>
    public bool IsEmpty => !IsBool && !IsString;
}
