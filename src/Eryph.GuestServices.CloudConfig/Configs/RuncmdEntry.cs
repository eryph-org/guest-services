namespace Eryph.GuestServices.CloudConfig;

[CloudInitRecord]
public sealed record RuncmdEntry
{
    public required bool IsShellCommand { get; init; }

    public string? Command { get; init; }

    public IReadOnlyList<string>? Argv { get; init; }
}
