#Requires -Version 5.1

# Azure VM Extension handler library.
#
# The Azure VM Agent invokes one of install.cmd / enable.cmd / disable.cmd /
# uninstall.cmd, each of which dispatches into Invoke-HandlerOperation.
# This module is also imported directly by the unit-test suite so individual
# lifecycle functions can be exercised without spinning up a real VM.
#
# All external mutations (sc.exe, Start-Service, & $ServiceBinary) go through
# the New-/Remove-EgsService and Invoke-EgsServiceStatus wrappers below.
# Tests mock those wrappers; the real lifecycle code stays unaware of the
# substitution.

Set-StrictMode -Version 3.0

$script:DefaultServiceName = 'eryph-guest-services'
$script:DefaultInstallRoot = 'C:\Program Files\eryph\guest-services'

function Get-EgsServiceName {
    if ($env:EGS_HANDLER_SERVICE_NAME) { return $env:EGS_HANDLER_SERVICE_NAME }
    return $script:DefaultServiceName
}

function Get-EgsInstallRoot {
    if ($env:EGS_HANDLER_INSTALL_ROOT) { return $env:EGS_HANDLER_INSTALL_ROOT }
    return $script:DefaultInstallRoot
}

function Get-EgsServiceBinary {
    Join-Path (Get-EgsInstallRoot) 'bin\egs-service.exe'
}

function Get-HandlerEnvironment {
    <#
    .SYNOPSIS
        Locates HandlerEnvironment.json which the Azure VM Agent drops next to
        the extension files, and returns the parsed `handlerEnvironment` block.

    .PARAMETER ScriptRoot
        Test-injectable override of $PSScriptRoot. The handler ships the JSON
        either one level above bin\ (production layout) or in bin\ itself
        (some Azure VM Agent builds).
    #>
    [CmdletBinding()]
    param(
        [Parameter()] [string] $ScriptRoot = $PSScriptRoot
    )

    $candidates = @(
        (Join-Path $ScriptRoot '..\HandlerEnvironment.json'),
        (Join-Path $ScriptRoot 'HandlerEnvironment.json')
    )
    foreach ($path in $candidates) {
        if (Test-Path -LiteralPath $path) {
            $parsed = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
            return $parsed[0].handlerEnvironment
        }
    }
    throw "HandlerEnvironment.json not found near $ScriptRoot"
}

function Get-LatestSettings {
    <#
    .SYNOPSIS
        Returns the highest-numbered N.settings file from the agent's configFolder.
        Sequence numbers are not necessarily contiguous; pick the largest int.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] $HandlerEnvironment
    )

    $configFolder = $HandlerEnvironment.configFolder
    if (-not (Test-Path -LiteralPath $configFolder)) {
        return @{ Sequence = 0; Public = @{} }
    }

    $latest = Get-ChildItem -LiteralPath $configFolder -Filter '*.settings' -ErrorAction SilentlyContinue |
        Sort-Object { [int]([IO.Path]::GetFileNameWithoutExtension($_.Name)) } -Descending |
        Select-Object -First 1
    if (-not $latest) { return @{ Sequence = 0; Public = @{} } }

    $raw = Get-Content -LiteralPath $latest.FullName -Raw | ConvertFrom-Json
    $public = $raw.runtimeSettings[0].handlerSettings.publicSettings
    return @{
        Sequence = [int]([IO.Path]::GetFileNameWithoutExtension($latest.Name))
        Public   = if ($null -eq $public) { @{} } else { $public }
    }
}

function Write-HandlerStatus {
    <#
    .SYNOPSIS
        Writes an Azure VM Extension status file atomically (.tmp → rename).
        Schema follows https://learn.microsoft.com/azure/virtual-machines/extensions/features-windows#handler-status.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] $HandlerEnvironment,
        [Parameter(Mandatory = $true)] [int] $Sequence,
        [Parameter(Mandatory = $true)] [string] $Operation,
        [Parameter(Mandatory = $true)] [ValidateSet('transitioning', 'success', 'error', 'warning')] [string] $Status,
        [Parameter(Mandatory = $true)] [int] $Code,
        [Parameter(Mandatory = $true)] [string] $Message
    )

    $payload = @(@{
        version       = '1.0'
        timestampUTC  = (Get-Date).ToUniversalTime().ToString('o')
        status        = @{
            name             = 'Eryph.GuestServices'
            operation        = $Operation
            status           = $Status
            code             = $Code
            formattedMessage = @{ lang = 'en-US'; message = $Message }
        }
    })

    $statusFolder = $HandlerEnvironment.statusFolder
    if (-not (Test-Path -LiteralPath $statusFolder)) {
        New-Item -ItemType Directory -Path $statusFolder -Force | Out-Null
    }
    $tmp = Join-Path $statusFolder "$Sequence.status.tmp"
    $final = Join-Path $statusFolder "$Sequence.status"
    $payload | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $tmp -Encoding UTF8
    Move-Item -LiteralPath $tmp -Destination $final -Force
}

function Write-HandlerLog {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] $HandlerEnvironment,
        [Parameter(Mandatory = $true)] [string] $Message
    )

    $logFolder = $HandlerEnvironment.logFolder
    if (-not (Test-Path -LiteralPath $logFolder)) {
        New-Item -ItemType Directory -Path $logFolder -Force | Out-Null
    }
    $line = "[{0}] {1}" -f (Get-Date).ToUniversalTime().ToString('o'), $Message
    $log = Join-Path $logFolder 'handler.log'
    Add-Content -LiteralPath $log -Value $line
}

function New-EgsService {
    <#
    .SYNOPSIS
        Registers egs-service as an auto-start Windows service. Wraps sc.exe so
        tests can mock the side effect without touching the host's SCM.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] [string] $ServiceName,
        [Parameter(Mandatory = $true)] [string] $BinaryPath
    )

    & sc.exe create $ServiceName start= auto binpath= "`"$BinaryPath`"" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "sc.exe create $ServiceName failed: $LASTEXITCODE" }
    & sc.exe failure $ServiceName reset= 60 actions= restart/10000 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "sc.exe failure $ServiceName failed: $LASTEXITCODE" }
}

function Remove-EgsService {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] [string] $ServiceName
    )

    & sc.exe delete $ServiceName | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "sc.exe delete $ServiceName failed: $LASTEXITCODE" }
}

function Invoke-EgsServiceStatus {
    <#
    .SYNOPSIS
        Calls `egs-service status --json` and returns a hashtable with
        ExitCode and (when parseable) the deserialized status object.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] [string] $BinaryPath
    )

    $raw = & $BinaryPath status --json 2>$null
    $exit = $LASTEXITCODE
    $obj = $null
    if ($exit -eq 0 -and $raw) {
        try { $obj = ($raw | Out-String) | ConvertFrom-Json } catch { $obj = $null }
    }
    return @{ ExitCode = $exit; Status = $obj }
}

function Invoke-InstallOperation {
    <#
    .SYNOPSIS
        Copies the extension payload to InstallRoot and registers the service.
        Idempotent: removes any existing install/service first.
    #>
    [CmdletBinding()]
    param(
        [Parameter()] [string] $ScriptRoot = $PSScriptRoot
    )

    $payloadDir = Join-Path $ScriptRoot '..\payload'
    if (-not (Test-Path -LiteralPath $payloadDir)) {
        throw "Extension payload missing at $payloadDir"
    }

    $serviceName = Get-EgsServiceName
    $installRoot = Get-EgsInstallRoot
    $serviceBinary = Get-EgsServiceBinary

    if (Test-Path -LiteralPath $installRoot) {
        $existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
        if ($existing) {
            Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
        }
        Remove-Item -LiteralPath $installRoot -Recurse -Force
    }
    New-Item -ItemType Directory -Path $installRoot -Force | Out-Null
    # `-Path` (not -LiteralPath) so the * wildcard expands. -LiteralPath
    # would treat 'payload\*' as a literal filename containing an asterisk
    # and find nothing.
    Copy-Item -Path (Join-Path $payloadDir '*') -Destination $installRoot -Recurse -Force

    if (-not (Get-Service -Name $serviceName -ErrorAction SilentlyContinue)) {
        New-EgsService -ServiceName $serviceName -BinaryPath $serviceBinary
    }
}

function Invoke-EnableOperation {
    <#
    .SYNOPSIS
        Starts egs-service and polls until provisioning reaches a terminal
        state. Honours the `skipCustomData` public setting.

    .PARAMETER Settings
        Output of Get-LatestSettings.

    .PARAMETER TimeoutMinutes
        How long to poll before giving up. Default 15 (Azure VM Extension
        guidance: enable must finish in under 30 minutes).

    .PARAMETER PollIntervalSeconds
        Test-injectable override (default 5).
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] $Settings,
        [Parameter()] [int] $TimeoutMinutes = 15,
        [Parameter()] [int] $PollIntervalSeconds = 5
    )

    $serviceName = Get-EgsServiceName
    $serviceBinary = Get-EgsServiceBinary

    $skip = $false
    if ($Settings.Public -is [System.Management.Automation.PSCustomObject]) {
        if ($Settings.Public.PSObject.Properties.Match('skipCustomData').Count -gt 0) {
            $skip = [bool]$Settings.Public.skipCustomData
        }
    } elseif ($Settings.Public -is [System.Collections.IDictionary]) {
        if ($Settings.Public.ContainsKey('skipCustomData')) {
            $skip = [bool]$Settings.Public['skipCustomData']
        }
    }
    if ($skip) {
        return @{ Status = 'success'; Message = 'Service installed; CustomData processing skipped per settings.' }
    }

    if ((Get-Service -Name $serviceName).Status -ne 'Running') {
        Start-Service -Name $serviceName
    }

    $deadline = (Get-Date).AddMinutes($TimeoutMinutes)
    while ((Get-Date) -lt $deadline) {
        $probe = Invoke-EgsServiceStatus -BinaryPath $serviceBinary
        if ($probe.ExitCode -eq 0 -and $probe.Status) {
            $state = $probe.Status.state
            switch ($state) {
                'completed' { return @{ Status = 'success'; Message = 'Provisioning completed.' } }
                'failed'    { return @{ Status = 'error';   Message = 'Provisioning failed; see %ProgramData%\eryph\provisioning\state.json.' } }
            }
        }
        Start-Sleep -Seconds $PollIntervalSeconds
    }
    return @{ Status = 'error'; Message = "Provisioning did not reach a terminal state within $TimeoutMinutes minutes." }
}

function Invoke-DisableOperation {
    [CmdletBinding()] param()

    $serviceName = Get-EgsServiceName
    $svc = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if ($svc -and $svc.Status -ne 'Stopped') {
        Stop-Service -Name $serviceName -Force
    }
}

function Invoke-UninstallOperation {
    [CmdletBinding()] param()

    $serviceName = Get-EgsServiceName
    $installRoot = Get-EgsInstallRoot

    Invoke-DisableOperation
    if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
        Remove-EgsService -ServiceName $serviceName
    }
    if (Test-Path -LiteralPath $installRoot) {
        Remove-Item -LiteralPath $installRoot -Recurse -Force
    }
}

function Invoke-HandlerOperation {
    <#
    .SYNOPSIS
        Top-level dispatcher invoked by the .cmd wrappers. Wraps the operation
        in a try/catch and writes transitioning + terminal status files.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [ValidateSet('Install', 'Enable', 'Disable', 'Uninstall')]
        [string] $Operation,

        [Parameter()] [string] $ScriptRoot = $PSScriptRoot
    )

    $handlerEnv = Get-HandlerEnvironment -ScriptRoot $ScriptRoot
    $settings = Get-LatestSettings -HandlerEnvironment $handlerEnv
    $sequence = $settings.Sequence

    try {
        Write-HandlerStatus -HandlerEnvironment $handlerEnv -Sequence $sequence `
            -Operation $Operation -Status 'transitioning' -Code 0 -Message "$Operation in progress"
        Write-HandlerLog -HandlerEnvironment $handlerEnv -Message "$Operation started (sequence $sequence)"

        switch ($Operation) {
            'Install'   { Invoke-InstallOperation -ScriptRoot $ScriptRoot; $result = @{ Status = 'success'; Message = 'Installed.' } }
            'Enable'    { $result = Invoke-EnableOperation -Settings $settings }
            'Disable'   { Invoke-DisableOperation;   $result = @{ Status = 'success'; Message = 'Disabled.' } }
            'Uninstall' { Invoke-UninstallOperation; $result = @{ Status = 'success'; Message = 'Uninstalled.' } }
        }

        $code = if ($result.Status -eq 'success') { 0 } else { 1 }
        Write-HandlerStatus -HandlerEnvironment $handlerEnv -Sequence $sequence `
            -Operation $Operation -Status $result.Status -Code $code -Message $result.Message
        Write-HandlerLog -HandlerEnvironment $handlerEnv `
            -Message "$Operation finished: $($result.Status) — $($result.Message)"
        return $code
    } catch {
        Write-HandlerStatus -HandlerEnvironment $handlerEnv -Sequence $sequence `
            -Operation $Operation -Status 'error' -Code 1 -Message $_.Exception.Message
        Write-HandlerLog -HandlerEnvironment $handlerEnv `
            -Message "$Operation crashed: $($_.Exception.Message)"
        return 1
    }
}

Export-ModuleMember -Function `
    Get-EgsServiceName, Get-EgsInstallRoot, Get-EgsServiceBinary, `
    Get-HandlerEnvironment, Get-LatestSettings, `
    Write-HandlerStatus, Write-HandlerLog, `
    New-EgsService, Remove-EgsService, Invoke-EgsServiceStatus, `
    Invoke-InstallOperation, Invoke-EnableOperation, `
    Invoke-DisableOperation, Invoke-UninstallOperation, `
    Invoke-HandlerOperation
