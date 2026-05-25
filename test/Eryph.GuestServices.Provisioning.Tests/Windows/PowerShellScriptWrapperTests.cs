using AwesomeAssertions;
using Eryph.GuestServices.Provisioning.Windows;

namespace Eryph.GuestServices.Provisioning.Tests.Windows;

/// <summary>
/// Direct tests for <see cref="PowerShellScriptWrapper"/> — the pure
/// string-building helper used by <c>ScriptsUserModule</c> to compose the
/// <c>-Command</c> argument that forces UTF-8 on the child PowerShell scope.
/// Without these, the wrapper is only exercised end-to-end via integration;
/// a stray apostrophe in the script path or a typo in an encoding setter
/// would surface only at runtime.
/// </summary>
public sealed class PowerShellScriptWrapperTests
{
    [Fact]
    public void EscapeSingleQuoted_doubles_apostrophes()
    {
        PowerShellScriptWrapper.EscapeSingleQuoted("don't").Should().Be("don''t");
    }

    [Fact]
    public void EscapeSingleQuoted_leaves_unrelated_chars_alone()
    {
        PowerShellScriptWrapper.EscapeSingleQuoted(@"C:\Path With Spaces\foo.ps1")
            .Should().Be(@"C:\Path With Spaces\foo.ps1");
    }

    [Fact]
    public void BuildScriptWrapper_sets_console_output_encoding_to_utf8()
    {
        var wrapper = PowerShellScriptWrapper.BuildScriptWrapper(@"C:\Temp\hello.ps1");

        // The console output encoding controls the stream that StandardOutputEncoding
        // decodes — must be UTF-8 to round-trip non-ASCII without mojibake.
        wrapper.Should().Contain("[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new();");
    }

    [Fact]
    public void BuildScriptWrapper_sets_pipeline_output_encoding_to_utf8()
    {
        var wrapper = PowerShellScriptWrapper.BuildScriptWrapper(@"C:\Temp\hello.ps1");

        // $OutputEncoding controls how PowerShell encodes when piping to native
        // commands. UTF-8 here keeps `Write-Output | Out-File` semantics honest.
        wrapper.Should().Contain("$OutputEncoding = [System.Text.UTF8Encoding]::new();");
    }

    [Fact]
    public void BuildScriptWrapper_sets_input_encoding_to_utf8()
    {
        var wrapper = PowerShellScriptWrapper.BuildScriptWrapper(@"C:\Temp\hello.ps1");
        wrapper.Should().Contain("$InputEncoding = [System.Text.UTF8Encoding]::new();");
    }

    [Fact]
    public void BuildScriptWrapper_single_quotes_the_script_path()
    {
        // Single-quoted paths are literal in PowerShell — no escape parsing of
        // backslashes or dollars, which matters for paths under C:\Users\$user
        // or with `$env:` patterns.
        var wrapper = PowerShellScriptWrapper.BuildScriptWrapper(@"C:\Temp\hello.ps1");
        wrapper.Should().Contain(@"& 'C:\Temp\hello.ps1'");
    }

    [Fact]
    public void BuildScriptWrapper_escapes_apostrophes_in_the_script_path()
    {
        // Apostrophes in folder names (e.g. "C:\Users\O'Connor\...") must be
        // doubled inside the single-quoted PS string.
        var wrapper = PowerShellScriptWrapper.BuildScriptWrapper(@"C:\Users\O'Connor\script.ps1");
        wrapper.Should().Contain(@"& 'C:\Users\O''Connor\script.ps1'");
        wrapper.Should().NotContain(@"& 'C:\Users\O'Connor");
    }

    [Fact]
    public void BuildScriptWrapper_propagates_LASTEXITCODE()
    {
        // Without `exit $LASTEXITCODE`, a script that fails with exit 5 would
        // surface to .NET as exit 0 (the implicit success of the outer
        // -Command scope). The wrapper must explicitly forward the inner
        // script's exit code.
        var wrapper = PowerShellScriptWrapper.BuildScriptWrapper(@"C:\Temp\hello.ps1");
        wrapper.Should().Contain("exit $LASTEXITCODE");
    }
}
