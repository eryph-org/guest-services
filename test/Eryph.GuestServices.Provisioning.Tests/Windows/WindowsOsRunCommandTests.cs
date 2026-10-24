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
        // %~1 strips the quotes cmd carries into the batch, proving the argument
        // value arrived intact.
        await File.WriteAllTextAsync(batch, "@echo off\r\necho GREET=%~1\r\n");
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

    [Fact]
    public async Task RunArgv_batch_argument_with_cmd_metacharacter_stays_literal()
    {
        if (!OperatingSystem.IsWindows()) return;

        // 'a&b' must reach the batch as a single argument. If it were passed
        // unquoted to cmd, the '&' would split the line and cmd would try to run
        // a second command 'b' (nonzero exit, 'a' only in %1).
        var os = new WindowsOs(NullLogger<WindowsOs>.Instance);
        var dir = Path.Combine(Path.GetTempPath(), "egs-cmd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var batch = Path.Combine(dir, "egs-echo.cmd");
        await File.WriteAllTextAsync(batch, "@echo off\r\necho VAL=%1\r\n");
        try
        {
            var result = await os.RunArgvCommandAsync([batch, "a&b"], CancellationToken.None);

            result.ExitCode.Should().Be(0);
            result.StdOut.Should().Contain("VAL=\"a&b\"");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task RunArgv_round_trips_non_ascii_batch_output()
    {
        if (!OperatingSystem.IsWindows()) return;

        // The batch is launched directly (no chance to inject chcp into it), so
        // the dispatch must set the cmd session to UTF-8 itself — otherwise echo
        // emits the OEM code page and our UTF-8 decode mangles non-ASCII.
        var os = new WindowsOs(NullLogger<WindowsOs>.Instance);
        var dir = Path.Combine(Path.GetTempPath(), "egs-cmd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var batch = Path.Combine(dir, "egs-echo.cmd");
        await File.WriteAllTextAsync(batch, "@echo off\r\necho VAL=%~1\r\n");
        try
        {
            var result = await os.RunArgvCommandAsync([batch, "Hällöchen"], CancellationToken.None);

            result.ExitCode.Should().Be(0);
            result.StdOut.Should().Contain("VAL=Hällöchen");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task RunArgv_rejects_a_batch_argument_that_would_inject_commands()
    {
        if (!OperatingSystem.IsWindows()) return;

        // An embedded quote would close cmd's quoting and expose the following
        // '&echo ...' as a separate command. RunArgvCommandAsync must reject it
        // before launch rather than run the injected command.
        var os = new WindowsOs(NullLogger<WindowsOs>.Instance);
        var dir = Path.Combine(Path.GetTempPath(), "egs-cmd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var batch = Path.Combine(dir, "egs-echo.cmd");
        await File.WriteAllTextAsync(batch, "@echo off\r\necho VAL=%1\r\n");
        try
        {
            var act = async () => await os.RunArgvCommandAsync(
                [batch, "a\"&echo PWNED&rem "], CancellationToken.None);

            await act.Should().ThrowAsync<ArgumentException>();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task RunArgv_handles_spaces_in_the_batch_path_and_arguments()
    {
        if (!OperatingSystem.IsWindows()) return;

        // Spaces in the batch path AND in an argument: cmd's default /c
        // outer-quote stripping mangles this unless the whole command is wrapped
        // and each token quoted.
        var os = new WindowsOs(NullLogger<WindowsOs>.Instance);
        var dir = Path.Combine(Path.GetTempPath(), "egs cmd " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var batch = Path.Combine(dir, "egs echo.cmd");
        await File.WriteAllTextAsync(batch, "@echo off\r\necho VAL=%1\r\n");
        try
        {
            var result = await os.RunArgvCommandAsync([batch, "arg with space"], CancellationToken.None);

            result.ExitCode.Should().Be(0);
            result.StdOut.Should().Contain("VAL=\"arg with space\"");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
