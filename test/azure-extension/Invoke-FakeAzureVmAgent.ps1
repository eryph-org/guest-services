#Requires -Version 5.1

<#
.SYNOPSIS
  Simulates the parts of the Azure VM Agent's extension-handling contract that
  are observable to the handler script.

.DESCRIPTION
  The real Azure VM Agent does many things — config-fetch, heartbeat, status
  reporting back to the platform — but from the extension handler's point of
  view, the contract is small:

    1. Drop the extension files into <pluginRoot>/<publisher>.<name>/<version>/
    2. Write HandlerEnvironment.json one level above the handler files
    3. Write N.settings into the configFolder (with the operator's settings)
    4. Invoke install.cmd, then enable.cmd, then on revoke disable.cmd, then
       uninstall.cmd. Each invocation is a normal child process.
    5. Read the .status file the handler atomically wrote and feed the
       observable state back to the platform.

  This simulator implements steps 2-5 against a caller-provided <BaseDir>.
  Step 1 is the caller's job — copy the unpacked extension into
  <BaseDir>/extension/ before invoking.

  By default the simulator runs WITHOUT system-mutating side effects: it
  honours $env:EGS_HANDLER_SERVICE_NAME / $env:EGS_HANDLER_INSTALL_ROOT so a
  CI run can scope service registration to a test-only name and a temp
  InstallRoot. The full lifecycle (Install -> Enable -> Disable -> Uninstall)
  is exercised; the resulting .status files are returned.

.PARAMETER BaseDir
  Working directory. Must already contain an unpacked extension under
  <BaseDir>/extension/ (root: HandlerManifest.json, install.cmd, bin/, payload/).
  The simulator creates config/, status/, log/, heartbeat/ siblings.

.PARAMETER PublicSettings
  Hashtable serialised into the runtimeSettings.handlerSettings.publicSettings
  block of the .settings file. Use $null or @{} for empty settings.

.PARAMETER Sequence
  Sequence number for the .settings/.status files. Default 0.

.PARAMETER Operation
  Lifecycle operation to invoke. Defaults to the full sequence
  Install -> Enable. Pass 'Full' to run all four.

.PARAMETER ServiceName
  Test-scoped service name. Defaults to 'eryph-guest-services-fake' so the
  simulator never collides with a real production service registration.

.PARAMETER InstallRoot
  Test-scoped install path. Defaults to <BaseDir>/install. Avoids touching
  C:\Program Files.

.PARAMETER NoExecuteHandlers
  When set, the simulator stages the env+settings files but does NOT invoke
  the handler. Useful when paired with a unit-test harness that drives
  Invoke-HandlerOperation directly.

.OUTPUTS
  Hashtable: @{
    BaseDir       = ...
    HandlerRoot   = ...
    ConfigFolder  = ...
    StatusFolder  = ...
    LogFolder     = ...
    Results       = @( @{ Operation; ExitCode; Status (parsed .status JSON) } ... )
  }
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $BaseDir,

    [Parameter()]
    [hashtable] $PublicSettings,

    [Parameter()]
    [int] $Sequence = 0,

    [Parameter()]
    [ValidateSet('Install', 'Enable', 'Disable', 'Uninstall', 'Full', 'InstallEnable')]
    [string] $Operation = 'InstallEnable',

    [Parameter()]
    [string] $ServiceName = 'eryph-guest-services-fake',

    [Parameter()]
    [string] $InstallRoot,

    [Parameter()]
    [switch] $NoExecuteHandlers
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0

$resolvedBase = (Resolve-Path -LiteralPath $BaseDir).Path
$handlerRoot = Join-Path $resolvedBase 'extension'
if (-not (Test-Path -LiteralPath $handlerRoot)) {
    throw "Expected unpacked extension at $handlerRoot. Copy the extension contents there first."
}
if (-not (Test-Path -LiteralPath (Join-Path $handlerRoot 'HandlerManifest.json'))) {
    throw "HandlerManifest.json missing from $handlerRoot — is this an unpacked extension?"
}

if (-not $InstallRoot) {
    $InstallRoot = Join-Path $resolvedBase 'install'
}

$configFolder    = Join-Path $resolvedBase 'config'
$statusFolder    = Join-Path $resolvedBase 'status'
$logFolder       = Join-Path $resolvedBase 'log'
$heartbeatFolder = Join-Path $resolvedBase 'heartbeat'

foreach ($d in @($configFolder, $statusFolder, $logFolder, $heartbeatFolder)) {
    if (-not (Test-Path -LiteralPath $d)) { New-Item -ItemType Directory -Path $d -Force | Out-Null }
}

# HandlerEnvironment.json sits one level ABOVE the .cmd files (where the Azure
# VM Agent writes it in the canonical C:\Packages\Plugins\<pub>.<name>\ layout).
# Handler.ps1 lives in bin/, so the JSON goes alongside the .cmd files at the
# extension root.
$handlerEnvPath = Join-Path $handlerRoot 'HandlerEnvironment.json'
$envPayload = @(@{
    version = 1
    handlerEnvironment = @{
        logFolder       = $logFolder
        configFolder    = $configFolder
        statusFolder    = $statusFolder
        heartbeatFile   = (Join-Path $heartbeatFolder 'heartbeat.json')
        deploymentid    = 'fake-deployment-id'
        rolename        = 'fake-role'
        instance        = 'fake-instance'
    }
})
$envPayload | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $handlerEnvPath -Encoding UTF8

$settingsPayload = @(@{
    runtimeSettings = @(@{
        handlerSettings = @{
            publicSettings    = if ($null -eq $PublicSettings) { @{} } else { $PublicSettings }
            protectedSettings = $null
        }
    })
})
$settingsPath = Join-Path $configFolder "$Sequence.settings"
$settingsPayload | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $settingsPath -Encoding UTF8

$ops = switch ($Operation) {
    'Install'       { @('Install') }
    'Enable'        { @('Enable') }
    'Disable'       { @('Disable') }
    'Uninstall'     { @('Uninstall') }
    'InstallEnable' { @('Install', 'Enable') }
    'Full'          { @('Install', 'Enable', 'Disable', 'Uninstall') }
}

$results = @()

if (-not $NoExecuteHandlers) {
    $prevSvcName     = $env:EGS_HANDLER_SERVICE_NAME
    $prevInstallRoot = $env:EGS_HANDLER_INSTALL_ROOT
    try {
        $env:EGS_HANDLER_SERVICE_NAME  = $ServiceName
        $env:EGS_HANDLER_INSTALL_ROOT  = $InstallRoot

        foreach ($op in $ops) {
            $cmdName = "$($op.ToLowerInvariant()).cmd"
            $cmdPath = Join-Path $handlerRoot $cmdName
            if (-not (Test-Path -LiteralPath $cmdPath)) {
                throw "Lifecycle command not found: $cmdPath"
            }

            $stdoutPath = Join-Path $logFolder "simulator-$op.stdout.log"
            $stderrPath = Join-Path $logFolder "simulator-$op.stderr.log"
            $proc = Start-Process -FilePath $cmdPath `
                -WorkingDirectory $handlerRoot `
                -NoNewWindow -Wait -PassThru `
                -RedirectStandardOutput $stdoutPath `
                -RedirectStandardError $stderrPath

            $statusFile = Join-Path $statusFolder "$Sequence.status"
            $parsedStatus = $null
            if (Test-Path -LiteralPath $statusFile) {
                $parsedStatus = Get-Content -LiteralPath $statusFile -Raw | ConvertFrom-Json
            }
            $results += [pscustomobject]@{
                Operation = $op
                ExitCode  = $proc.ExitCode
                Status    = $parsedStatus
                Stdout    = $stdoutPath
                Stderr    = $stderrPath
            }
            if ($proc.ExitCode -ne 0) {
                Write-Warning "Lifecycle operation '$op' exited with code $($proc.ExitCode). See $stdoutPath / $stderrPath."
                # Continue running the rest so the caller can inspect later
                # operations too (e.g. Uninstall cleaning up after a failed Enable).
            }
        }
    }
    finally {
        $env:EGS_HANDLER_SERVICE_NAME = $prevSvcName
        $env:EGS_HANDLER_INSTALL_ROOT = $prevInstallRoot
    }
}

return @{
    BaseDir       = $resolvedBase
    HandlerRoot   = $handlerRoot
    ConfigFolder  = $configFolder
    StatusFolder  = $statusFolder
    LogFolder     = $logFolder
    Results       = $results
}
