namespace Eryph.GuestServices.Provisioning.Windows;

public sealed record RunCommandResult(int ExitCode, string StdOut, string StdErr);
