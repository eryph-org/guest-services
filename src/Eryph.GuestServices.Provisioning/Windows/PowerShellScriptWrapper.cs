namespace Eryph.GuestServices.Provisioning.Windows;

/// <summary>
/// Pure string-building helpers for composing the <c>-Command</c> argument
/// passed to <c>powershell.exe</c>. Kept platform-agnostic (no
/// <see cref="System.Runtime.Versioning.SupportedOSPlatformAttribute"/>) so
/// non-Windows-scoped callers (modules, tests) can invoke it without CA1416
/// noise.
/// </summary>
internal static class PowerShellScriptWrapper
{
    /// <summary>
    /// Escapes a value for embedding in a PowerShell single-quoted string.
    /// PowerShell single-quoted strings escape an apostrophe by doubling it;
    /// there is no other special character to handle.
    /// </summary>
    public static string EscapeSingleQuoted(string value) => value.Replace("'", "''");

    /// <summary>
    /// Builds the <c>-Command</c> argument that runs <paramref name="scriptPath"/>
    /// inside a UTF-8-configured PowerShell scope. The wrapper forces UTF-8 on
    /// every encoding hook PowerShell exposes BEFORE the user's script runs,
    /// so non-ASCII stdout/stderr survive .NET-side decoding with
    /// <c>StandardOutputEncoding = UTF8</c>.
    /// </summary>
    public static string BuildScriptWrapper(string scriptPath)
    {
        var escaped = EscapeSingleQuoted(scriptPath);
        // We use single quotes around the script path because PowerShell's `&`
        // call operator treats single-quoted strings as literal (no expansion).
        return "& { "
            + "[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new(); "
            + "$OutputEncoding = [System.Text.UTF8Encoding]::new(); "
            + "$InputEncoding = [System.Text.UTF8Encoding]::new(); "
            + $"& '{escaped}' ; "
            + "exit $LASTEXITCODE }";
    }
}
