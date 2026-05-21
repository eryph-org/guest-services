namespace Eryph.GuestServices.Provisioning.Windows;

public sealed record LocalUserSpec
{
    public required string Name { get; init; }

    public string? FullName { get; init; }

    public string? Comment { get; init; }

    public bool? Disabled { get; init; }

    public string? HomeDir { get; init; }
}
