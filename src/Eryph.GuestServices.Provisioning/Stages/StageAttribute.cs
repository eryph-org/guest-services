namespace Eryph.GuestServices.Provisioning.Stages;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class StageAttribute(Stage stage) : Attribute
{
    public Stage Stage { get; } = stage;

    // Lower values run before higher within the same stage (default = 0).
    public int Order { get; init; }
}
