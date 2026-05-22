using System.Text;
using AwesomeAssertions;
using Eryph.GuestServices.Provisioning.Tests.Reporting;
using Eryph.GuestServices.Provisioning.UserData;
using Microsoft.Extensions.Logging;

namespace Eryph.GuestServices.Provisioning.Tests.UserData;

public sealed class ScriptKindDetectorTests
{
    [Fact]
    public void Detect_Ps1Filename_NoShebang_ReturnsPowerShell()
    {
        // Real eryph gene shape: filename=enable_rd.ps1, no shebang.
        // cbi dispatches by extension; we must do the same.
        var body = Encoding.UTF8.GetBytes("Set-ItemProperty -Path 'HKLM:\\foo' -Value 0\n");
        var logger = new CapturingLogger<object>();

        var kind = ScriptKindDetector.Detect("enable_rd.ps1", body, "text/x-shellscript", logger);

        kind.Should().Be(ScriptKind.PowerShell);
        logger.Entries.Should().BeEmpty();
    }

    [Theory]
    [InlineData("run.cmd", ScriptKind.Cmd)]
    [InlineData("run.bat", ScriptKind.Cmd)]
    [InlineData("setup.PS1", ScriptKind.PowerShell)] // case-insensitive
    public void Detect_KnownExtensions_DispatchByFilename(string filename, ScriptKind expected)
    {
        var logger = new CapturingLogger<object>();
        var kind = ScriptKindDetector.Detect(filename, Encoding.UTF8.GetBytes("echo hi"), "text/x-shellscript", logger);
        kind.Should().Be(expected);
    }

    // Real-world quirk: cbi-incompatible "hand-written cloud-config" shape —
    // text/x-shellscript with no filename and no shebang. RFC 0007 says we
    // accept it best-effort as PowerShell on Windows AND log a warning so the
    // operator notices. This is the cbi-bug shape we deliberately accept.
    [Fact]
    public void Detect_NoFilenameNoShebang_TextXShellscript_FallsBackToPowerShellAndLogsWarning()
    {
        var logger = new CapturingLogger<object>();
        var body = Encoding.UTF8.GetBytes("Write-Host hello\n");

        var kind = ScriptKindDetector.Detect(null, body, "text/x-shellscript", logger);

        kind.Should().Be(ScriptKind.PowerShell);
        logger.Entries.Should().ContainSingle(e =>
            e.Level == LogLevel.Warning && e.Message.Contains("falling back to PowerShell"));
    }

    // Real-world quirk: a .sh filename on a Windows guest. We cannot run it
    // (no POSIX shell); detector returns Other and logs a warning so the
    // operator sees an audit trail rather than a silent drop.
    [Fact]
    public void Detect_ShFilenameOnWindows_ReturnsOtherAndLogsWarning()
    {
        var logger = new CapturingLogger<object>();
        var body = Encoding.UTF8.GetBytes("echo hello\n");

        var kind = ScriptKindDetector.Detect("install.sh", body, "text/x-shellscript", logger);

        kind.Should().Be(ScriptKind.Other);
        logger.Entries.Should().ContainSingle(e =>
            e.Level == LogLevel.Warning && e.Message.Contains(".sh"));
    }

    [Fact]
    public void Detect_PowerShellShebang_NoFilename_ReturnsPowerShell()
    {
        var logger = new CapturingLogger<object>();
        var body = Encoding.UTF8.GetBytes("#ps1_sysnative\nWrite-Host hi\n");

        var kind = ScriptKindDetector.Detect(null, body, "text/x-shellscript", logger);

        kind.Should().Be(ScriptKind.PowerShell);
        logger.Entries.Should().BeEmpty();
    }

    [Fact]
    public void Detect_PosixShebangOnWindows_ReturnsOtherAndLogsWarning()
    {
        var logger = new CapturingLogger<object>();
        var body = Encoding.UTF8.GetBytes("#!/bin/bash\necho hi\n");

        var kind = ScriptKindDetector.Detect(null, body, "text/x-shellscript", logger);

        kind.Should().Be(ScriptKind.Other);
        logger.Entries.Should().ContainSingle(e =>
            e.Level == LogLevel.Warning && e.Message.Contains("POSIX shebang"));
    }

    // No signal at all: filename without a recognised extension, no shebang,
    // and a content-type that isn't text/x-shellscript. Detector returns Other
    // but MUST log so the operator isn't surprised by a silent drop.
    [Fact]
    public void Detect_NoSignals_ReturnsOtherAndLogsWarning()
    {
        var logger = new CapturingLogger<object>();
        var body = Encoding.UTF8.GetBytes("some opaque content\n");

        var kind = ScriptKindDetector.Detect("note.txt", body, "text/plain", logger);

        kind.Should().Be(ScriptKind.Other);
        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning);
    }
}
