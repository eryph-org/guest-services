using System.Runtime.Versioning;
using AwesomeAssertions;
using Eryph.GuestServices.Provisioning.Windows;
using Microsoft.Extensions.Logging.Abstractions;

namespace Eryph.GuestServices.Provisioning.Tests.Windows;

/// <summary>
/// Coverage for <see cref="WindowsOs.RunArgvCommandAsync"/> dispatch. Touches the
/// real process launcher, so gated by <see cref="OperatingSystem.IsWindows"/>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsOsRunCommandTests
{
    [Fact]
    public async Task RunArgv_dispatches_a_batch_file_through_cmd()
    {
        if (!OperatingSystem.IsWindows()) return;

        // A just-installed tool often resolves to a .cmd shim (e.g. npm.cmd).
        // CreateProcess cannot launch a batch file directly; RunArgvCommandAsync
        // must route it through cmd.exe. Without that, Process.Start throws.
        var os = new WindowsOs(NullLogger<WindowsOs>.Instance);
        var dir = Path.Combine(Path.GetTempPath(), "egs-cmd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var batch = Path.Combine(dir, "egs-greet.cmd");
        await File.WriteAllTextAsync(batch, "@echo off\r\necho GREET=%1\r\n");
        try
        {
            var result = await os.RunArgvCommandAsync([batch, "hello"], CancellationToken.None);

            result.ExitCode.Should().Be(0);
            result.StdOut.Should().Contain("GREET=hello");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
