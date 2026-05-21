Set-StrictMode -Version Latest

# Asserts that a captured Invoke-EgsProvisioning result has the expected exit
# code. On mismatch, the failure message embeds stdout + stderr so the test
# report points directly at what the binary said.
function Assert-EgsExitCode {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [pscustomobject] $Result,
        [Parameter(Mandatory)] [int] $Expected
    )

    if ($Result.ExitCode -ne $Expected) {
        $message = @(
            "Expected exit code $Expected but got $($Result.ExitCode).",
            "Command: egs-provisioning $($Result.Arguments -join ' ')",
            "--- stdout ---",
            $Result.StdOut.TrimEnd(),
            "--- stderr ---",
            $Result.StdErr.TrimEnd()
        ) -join [Environment]::NewLine
        throw $message
    }
}

# Asserts that the combined stdout+stderr from a run contains the expected
# substring. Useful for verifying that a module emitted its expected log line.
function Assert-EgsOutputContains {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [pscustomobject] $Result,
        [Parameter(Mandatory)] [string] $Substring
    )

    $haystack = ($Result.StdOut + [Environment]::NewLine + $Result.StdErr)
    if ($haystack -notlike "*$Substring*") {
        throw @(
            "Expected output to contain '$Substring' but it did not.",
            "Command: egs-provisioning $($Result.Arguments -join ' ')",
            "--- stdout ---",
            $Result.StdOut.TrimEnd(),
            "--- stderr ---",
            $Result.StdErr.TrimEnd()
        ) -join [Environment]::NewLine
    }
}

function Assert-EgsStderrContains {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [pscustomobject] $Result,
        [Parameter(Mandatory)] [string] $Substring
    )

    if ($Result.StdErr -notlike "*$Substring*") {
        throw @(
            "Expected stderr to contain '$Substring' but it did not.",
            "Command: egs-provisioning $($Result.Arguments -join ' ')",
            "--- stdout ---",
            $Result.StdOut.TrimEnd(),
            "--- stderr ---",
            $Result.StdErr.TrimEnd()
        ) -join [Environment]::NewLine
    }
}

# Build a fresh, isolated state directory for a test. The harness points the
# agent at this directory via --state-dir <path> (the CLI is expected to
# accept that switch — see test/Pester/README.md for the contract). On test
# teardown the directory is removed.
function New-IsolatedStateDir {
    [CmdletBinding()]
    param()

    $dir = Join-Path ([System.IO.Path]::GetTempPath()) ("egs-state-" + [Guid]::NewGuid().ToString('N'))
    [void] (New-Item -ItemType Directory -Path $dir)
    return $dir
}

function Remove-IsolatedStateDir {
    [CmdletBinding()]
    param([Parameter(Mandatory)] [string] $Path)

    if (Test-Path -LiteralPath $Path) {
        Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction SilentlyContinue
    }
}
