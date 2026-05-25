Set-StrictMode -Version Latest

# Locate the egs-service.exe produced by `dotnet build`. The harness defaults
# to the Debug/net10.0/win-x64 layout that matches a vanilla `dotnet build` of
# src/Eryph.GuestServices.Service. Set the EGS_SERVICE_EXE environment variable
# to override (useful for testing a self-contained publish or a Release build).
# EGS_PROVISIONING_EXE is still honored for backwards compatibility.
function Get-EgsProvisioningExePath {
    [CmdletBinding()]
    param(
        [string] $Configuration = 'Debug',
        [string] $TargetFramework = 'net10.0',
        [string] $RuntimeIdentifier = 'win-x64'
    )

    $override = $env:EGS_SERVICE_EXE
    if (-not $override) { $override = $env:EGS_PROVISIONING_EXE }

    if ($override) {
        if (-not (Test-Path -LiteralPath $override)) {
            throw "EGS_SERVICE_EXE points at '$override' but that file does not exist."
        }
        return (Resolve-Path -LiteralPath $override).Path
    }

    $root = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))
    $exe = Join-Path $root "src\Eryph.GuestServices.Service\bin\$Configuration\$TargetFramework\$RuntimeIdentifier\egs-service.exe"

    if (-not (Test-Path -LiteralPath $exe)) {
        throw "egs-service.exe not found at '$exe'. Build the service first ('dotnet build src\Eryph.GuestServices.Service') or set EGS_SERVICE_EXE."
    }

    return (Resolve-Path -LiteralPath $exe).Path
}

# Invoke egs-service.exe with the supplied arguments. Captures stdout, stderr,
# and the exit code. Returns a structured object the assertion helpers in
# Test-Provisioning.ps1 understand.
#
# The wrapper deliberately avoids `Invoke-Expression` / `&` with array
# splatting so callers see the exact arg vector that was passed.
function Invoke-EgsProvisioning {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string[]] $Arguments,

        [int] $TimeoutSeconds = 60,

        [string] $WorkingDirectory
    )

    $exe = Get-EgsProvisioningExePath
    $stdoutFile = [System.IO.Path]::GetTempFileName()
    $stderrFile = [System.IO.Path]::GetTempFileName()

    try {
        $startInfo = New-Object System.Diagnostics.ProcessStartInfo
        $startInfo.FileName = $exe
        foreach ($arg in $Arguments) {
            [void] $startInfo.ArgumentList.Add($arg)
        }
        $startInfo.RedirectStandardOutput = $true
        $startInfo.RedirectStandardError = $true
        $startInfo.UseShellExecute = $false
        $startInfo.CreateNoWindow = $true
        if ($WorkingDirectory) { $startInfo.WorkingDirectory = $WorkingDirectory }

        $process = [System.Diagnostics.Process]::Start($startInfo)

        # Read both streams asynchronously to avoid the 4KB pipe deadlock
        # that .NET inherits from the Win32 CreateProcess contract.
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()

        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            try { $process.Kill($true) } catch { }
            throw "egs-service.exe did not exit within $TimeoutSeconds seconds (args: $($Arguments -join ' '))."
        }

        $stdoutText = $stdoutTask.GetAwaiter().GetResult()
        $stderrText = $stderrTask.GetAwaiter().GetResult()

        return [pscustomobject]@{
            ExitCode  = $process.ExitCode
            StdOut    = $stdoutText
            StdErr    = $stderrText
            Arguments = $Arguments
            ExePath   = $exe
        }
    }
    finally {
        Remove-Item -LiteralPath $stdoutFile -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $stderrFile -ErrorAction SilentlyContinue
    }
}

# Materialise a sample whose body contains the {{SAMPLES_DIR}} token (e.g.
# the 06-include-url.txt fixture). Returns the path to the rewritten copy in
# a temp directory; the caller is responsible for cleaning the temp dir up.
function Resolve-SampleTemplate {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $TemplatePath,

        [Parameter(Mandatory)]
        [string] $SamplesDirectory
    )

    if (-not (Test-Path -LiteralPath $TemplatePath)) {
        throw "Template '$TemplatePath' not found."
    }

    $tempDir = Join-Path ([System.IO.Path]::GetTempPath()) ("egs-pester-" + [Guid]::NewGuid().ToString('N'))
    [void] (New-Item -ItemType Directory -Path $tempDir)

    $body = Get-Content -LiteralPath $TemplatePath -Raw
    # Convert backslashes to forward slashes so the path is URL-safe inside
    # the file:// URL emitted by the 06-include-url.txt fixture.
    $urlPath = ($SamplesDirectory -replace '\\', '/').TrimEnd('/')
    $rendered = $body.Replace('{{SAMPLES_DIR}}', $urlPath)

    $outFile = Join-Path $tempDir (Split-Path -Leaf $TemplatePath)
    Set-Content -LiteralPath $outFile -Value $rendered -Encoding UTF8 -NoNewline
    return $outFile
}
