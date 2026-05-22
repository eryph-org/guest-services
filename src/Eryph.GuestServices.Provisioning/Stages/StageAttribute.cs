namespace Eryph.GuestServices.Provisioning.Stages;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class StageAttribute(Stage stage) : Attribute
{
    public Stage Stage { get; } = stage;

    // Lower values run before higher within the same stage (default = 0).
    public int Order { get; init; }

    /// <summary>
    /// How often the module is allowed to run. Defaults to
    /// <see cref="ModuleFrequency.PerInstance"/>, matching cloud-init's default.
    /// Make this explicit on every module — the choice should be deliberate
    /// (per-instance config, per-boot housekeeping, per-once seed).
    /// </summary>
    public ModuleFrequency Frequency { get; init; } = ModuleFrequency.PerInstance;
}
