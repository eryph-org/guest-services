namespace Eryph.GuestServices.Provisioning.Stages;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class StageAttribute(Stage stage) : Attribute
{
    public Stage Stage { get; } = stage;

    // Lower values run before higher within the same stage (default = 0).
    public int Order { get; init; }

    /// <summary>
    /// Human-readable module name used in the provisioning report (the KVP
    /// status / cloud-init event stream), e.g. <c>users-groups</c>. When null
    /// the StageRunner derives one from the type name (strip the "Module"
    /// suffix, kebab-case) — set this only to pin a name that differs from the
    /// derived one, typically to match cloud-init's canonical module name
    /// (e.g. <c>ntp</c> for <c>NtpClientModule</c>). The C# type's full name
    /// remains the internal key for semaphores/state; this is report-only.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// How often the module is allowed to run. Defaults to
    /// <see cref="ModuleFrequency.PerInstance"/>, matching cloud-init's default.
    /// Make this explicit on every module — the choice should be deliberate
    /// (per-instance config, per-boot housekeeping, per-once seed).
    /// </summary>
    public ModuleFrequency Frequency { get; init; } = ModuleFrequency.PerInstance;
}
