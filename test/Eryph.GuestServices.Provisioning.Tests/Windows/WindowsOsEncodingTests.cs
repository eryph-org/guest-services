using System.Runtime.Versioning;
using AwesomeAssertions;
using Eryph.GuestServices.Provisioning.Windows;
using Microsoft.Extensions.Logging.Abstractions;

namespace Eryph.GuestServices.Provisioning.Tests.Windows;

/// <summary>
/// Round-trip integration tests for the UTF-8 fix on child-process stdout.
/// These tests must actually spawn child processes, so they are gated by
/// <see cref="OperatingSystem.IsWindows"/> early-return (the repo convention).
/// They cover the three contracts that broke before the fix: powershell.exe
/// emitting non-ASCII stdout, cmd.exe via temp-script emitting non-ASCII,
/// and powershell.exe propagating a non-zero exit with non-ASCII stderr.
/// </summary>
[SupportedOSPlatform("windows")]
[Trait("Category", "Integration")]
public sealed class WindowsOsEncodingTests
{
    private static WindowsOs CreateOs() => new(NullLogger<WindowsOs>.Instance);

    // Pin a recognisable non-ASCII string so a mojibake regression is obvious
    // in the failure diff. German umlauts cover both 2-byte UTF-8 sequences
    // (ä, ö, ü) and a typical OEM/CP1252 vs UTF-8 collision.
    private const string NonAscii = "Hällöchen";

    [Fact]
    public async Task PowerShellScript_OutputsNonAscii_CapturedAsUtf8()
    {
        if (!OperatingSystem.IsWindows()) return;

        var scriptPath = Path.Combine(
            Path.GetTempPath(),
            $"egs-encoding-{Guid.NewGuid():N}.ps1");
        // The script file itself must be UTF-8 (with BOM is safest for PS5);
        // otherwise PS5 misinterprets the Hällöchen literal at parse time.
        await File.WriteAllTextAsync(
            scriptPath,
            $"Write-Output '{NonAscii}'\r\n",
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        try
        {
            // We invoke through the same wrapper ScriptsUserModule uses so we
            // exercise the whole pipeline (StandardOutputEncoding + PS-side
            // encoding setters).
            var wrapper = PowerShellScriptWrapper.BuildScriptWrapper(scriptPath);
            var result = await CreateOs().RunArgvCommandAsync(
                [
                    "powershell.exe", "-NoProfile", "-NonInteractive",
                    "-ExecutionPolicy", "Bypass",
                    "-Command", wrapper,
                ],
                CancellationToken.None);

            result.ExitCode.Should().Be(0);
            result.StdOut.Trim().Should().Be(NonAscii,
                "PowerShell stdout must round-trip non-ASCII through UTF-8 decoding");
        }
        finally
        {
            try { File.Delete(scriptPath); } catch { }
        }
    }

    [Fact]
    public async Task CmdScript_OutputsNonAscii_CapturedAsUtf8()
    {
        if (!OperatingSystem.IsWindows()) return;

        // RunShellCommandAsync writes a temp .cmd file that prepends
        // `chcp 65001 >nul` so cmd.exe emits UTF-8.
        var result = await CreateOs().RunShellCommandAsync(
            $"echo {NonAscii}",
            CancellationToken.None);

        result.ExitCode.Should().Be(0);
        result.StdOut.Trim().Should().Be(NonAscii,
            "cmd.exe stdout must round-trip non-ASCII via chcp 65001 + UTF-8 decoding");
    }

    [Fact]
    public async Task PowerShellScript_NonZeroExit_PreservesUtf8Stderr()
    {
        if (!OperatingSystem.IsWindows()) return;

        var scriptPath = Path.Combine(
            Path.GetTempPath(),
            $"egs-encoding-err-{Guid.NewGuid():N}.ps1");
        // Write to stderr explicitly via [Console]::Error.WriteLine — the
        // PowerShell Write-Error path adds extra noise that obscures the
        // round-trip we want to verify.
        await File.WriteAllTextAsync(
            scriptPath,
            $"[Console]::Error.WriteLine('{NonAscii}')\r\nexit 1\r\n",
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        try
        {
            var wrapper = PowerShellScriptWrapper.BuildScriptWrapper(scriptPath);
            var result = await CreateOs().RunArgvCommandAsync(
                [
                    "powershell.exe", "-NoProfile", "-NonInteractive",
                    "-ExecutionPolicy", "Bypass",
                    "-Command", wrapper,
                ],
                CancellationToken.None);

            result.ExitCode.Should().Be(1, "the wrapper must propagate $LASTEXITCODE");
            result.StdErr.Should().Contain(NonAscii,
                "stderr must round-trip non-ASCII through UTF-8 decoding even on failure");
        }
        finally
        {
            try { File.Delete(scriptPath); } catch { }
        }
    }
}
