#Requires -Version 7.0
#Requires -Modules @{ ModuleName='Pester'; ModuleVersion='5.5.0' }

# Pester tests for Invoke-FakeAzureVmAgent.ps1.
#
# Two kinds of coverage live here:
#
#   1. Contract tests against the simulator's -NoExecuteHandlers staging
#      output: HandlerEnvironment.json + N.settings have the shape the real
#      Azure VM Agent documents.
#
#   2. End-to-end-on-the-host: run Invoke-HandlerOperation against the
#      simulator's staged dirs with all system-mutating cmdlets mocked.
#      This exercises Get-HandlerEnvironment + Get-LatestSettings + the
#      dispatcher together, on a real filesystem, without needing a VM.
#
# Service-registration side effects are NEVER exercised on the host. Real
# .cmd -> powershell.exe -> sc.exe is the job of an in-VM smoke test.

BeforeAll {
    $script:repoRoot = (Resolve-Path "$PSScriptRoot/../..").Path
    $script:simulator = Join-Path $PSScriptRoot 'Invoke-FakeAzureVmAgent.ps1'
    $script:extensionSrc = Join-Path $script:repoRoot 'packaging\azure-extension'
    $script:modulePath = (Resolve-Path "$script:extensionSrc/bin/HandlerLib.psm1").Path
    Import-Module $script:modulePath -Force
    $script:moduleName = 'HandlerLib'
}

AfterAll {
    Remove-Module -Name $script:moduleName -Force -ErrorAction SilentlyContinue
}

Describe 'Invoke-FakeAzureVmAgent.ps1 -NoExecuteHandlers staging' {
    BeforeEach {
        $script:base = New-Item -ItemType Directory `
            -Path (Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))) -Force
        $script:extension = New-Item -ItemType Directory `
            -Path (Join-Path $script:base 'extension') -Force
        # Mirror the real extension layout without the egs-service payload (we
        # are only testing the staging contract, not the install copy).
        Copy-Item -LiteralPath (Join-Path $script:extensionSrc 'HandlerManifest.json') `
            -Destination $script:extension
        Copy-Item -Path (Join-Path $script:extensionSrc '*.cmd') `
            -Destination $script:extension
        Copy-Item -LiteralPath (Join-Path $script:extensionSrc 'bin') `
            -Destination $script:extension -Recurse
    }

    It 'writes HandlerEnvironment.json with the documented field set' {
        $result = & $script:simulator -BaseDir $script:base -NoExecuteHandlers
        $envPath = Join-Path $result.HandlerRoot 'HandlerEnvironment.json'
        Test-Path -LiteralPath $envPath | Should -BeTrue

        $parsed = Get-Content -LiteralPath $envPath -Raw | ConvertFrom-Json
        $parsed | Should -HaveCount 1
        $parsed[0].version | Should -Be 1
        $env = $parsed[0].handlerEnvironment
        $env.logFolder      | Should -Be $result.LogFolder
        $env.configFolder   | Should -Be $result.ConfigFolder
        $env.statusFolder   | Should -Be $result.StatusFolder
        $env.heartbeatFile  | Should -Match 'heartbeat\.json$'
    }

    It 'writes the requested PublicSettings into N.settings' {
        $public = @{ skipCustomData = $false; foo = 'bar' }
        $result = & $script:simulator -BaseDir $script:base `
            -PublicSettings $public -Sequence 3 -NoExecuteHandlers

        $settingsPath = Join-Path $result.ConfigFolder '3.settings'
        Test-Path -LiteralPath $settingsPath | Should -BeTrue

        $parsed = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
        $rt = $parsed[0].runtimeSettings[0].handlerSettings
        $rt.publicSettings.foo            | Should -Be 'bar'
        $rt.publicSettings.skipCustomData | Should -Be $false
    }

    It 'defaults to empty publicSettings when PublicSettings is omitted' {
        $result = & $script:simulator -BaseDir $script:base -NoExecuteHandlers
        $parsed = Get-Content -LiteralPath (Join-Path $result.ConfigFolder '0.settings') -Raw |
            ConvertFrom-Json
        $rt = $parsed[0].runtimeSettings[0].handlerSettings
        # ConvertTo-Json renders @{} as `@{}` which round-trips to a PSCustomObject
        # with zero properties.
        @($rt.publicSettings.PSObject.Properties).Count | Should -Be 0
    }

    It 'creates config/, status/, log/, heartbeat/ siblings of extension/' {
        $result = & $script:simulator -BaseDir $script:base -NoExecuteHandlers
        Test-Path -LiteralPath $result.ConfigFolder | Should -BeTrue
        Test-Path -LiteralPath $result.StatusFolder | Should -BeTrue
        Test-Path -LiteralPath $result.LogFolder    | Should -BeTrue
        (Get-Item $result.ConfigFolder).Parent.FullName |
            Should -Be (Get-Item $result.HandlerRoot).Parent.FullName
    }

    It 'rejects a BaseDir that does not contain extension/HandlerManifest.json' {
        Remove-Item -LiteralPath (Join-Path $script:extension 'HandlerManifest.json') -Force
        { & $script:simulator -BaseDir $script:base -NoExecuteHandlers } |
            Should -Throw -ExpectedMessage '*HandlerManifest.json missing*'
    }
}

Describe 'Invoke-HandlerOperation against simulator-staged layout (no .cmd cross)' {
    # This exercises the FULL Handler.ps1 control flow without the .cmd ->
    # powershell.exe boundary: real Get-HandlerEnvironment + Get-LatestSettings
    # against on-disk JSON, real dispatcher, real Write-HandlerStatus. The
    # only thing mocked are the service-touching wrappers — keeping the host
    # clean.

    BeforeEach {
        $script:base = New-Item -ItemType Directory `
            -Path (Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))) -Force
        $script:extension = New-Item -ItemType Directory `
            -Path (Join-Path $script:base 'extension') -Force
        Copy-Item -LiteralPath (Join-Path $script:extensionSrc 'HandlerManifest.json') `
            -Destination $script:extension
        Copy-Item -Path (Join-Path $script:extensionSrc '*.cmd') `
            -Destination $script:extension
        Copy-Item -LiteralPath (Join-Path $script:extensionSrc 'bin') `
            -Destination $script:extension -Recurse

        # Stub a payload so Invoke-InstallOperation has something to copy.
        $payloadBin = New-Item -ItemType Directory `
            -Path (Join-Path $script:extension 'payload\bin') -Force
        Set-Content -LiteralPath (Join-Path $payloadBin 'egs-service.exe') -Value 'fake-exe'

        # Stage env + settings via the simulator without invoking handlers.
        $script:staging = & $script:simulator -BaseDir $script:base -NoExecuteHandlers
        $script:handlerBin = Join-Path $script:staging.HandlerRoot 'bin'

        $env:EGS_HANDLER_INSTALL_ROOT = Join-Path $script:base 'install'
        $env:EGS_HANDLER_SERVICE_NAME = 'eryph-guest-services-test'

        # No-op every service-touching call so the host is untouched.
        Mock -ModuleName $script:moduleName Get-Service { $null }
        Mock -ModuleName $script:moduleName Stop-Service {}
        Mock -ModuleName $script:moduleName Start-Service {}
        Mock -ModuleName $script:moduleName New-EgsService {}
        Mock -ModuleName $script:moduleName Remove-EgsService {}
        Mock -ModuleName $script:moduleName Invoke-EgsServiceStatus {
            @{ ExitCode = 0; Status = [pscustomobject]@{ state = 'completed' } }
        }
        Mock -ModuleName $script:moduleName Start-Sleep {}
    }

    AfterEach {
        $env:EGS_HANDLER_INSTALL_ROOT = $null
        $env:EGS_HANDLER_SERVICE_NAME = $null
    }

    It 'Install writes a success .status under the simulator-issued sequence' {
        $code = Invoke-HandlerOperation -Operation 'Install' -ScriptRoot $script:handlerBin
        $code | Should -Be 0

        $statusFile = Join-Path $script:staging.StatusFolder '0.status'
        Test-Path -LiteralPath $statusFile | Should -BeTrue

        $parsed = Get-Content -LiteralPath $statusFile -Raw | ConvertFrom-Json
        $parsed[0].status.operation | Should -Be 'Install'
        $parsed[0].status.status    | Should -Be 'success'

        # Side effect on the test-only InstallRoot.
        Test-Path -LiteralPath (Join-Path $env:EGS_HANDLER_INSTALL_ROOT 'bin\egs-service.exe') |
            Should -BeTrue
    }

    It 'Enable honours skipCustomData public setting end-to-end' {
        # Re-stage with skipCustomData=true.
        Remove-Item -LiteralPath $script:base -Recurse -Force
        $script:base = New-Item -ItemType Directory `
            -Path (Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))) -Force
        $script:extension = New-Item -ItemType Directory `
            -Path (Join-Path $script:base 'extension') -Force
        Copy-Item -LiteralPath (Join-Path $script:extensionSrc 'HandlerManifest.json') `
            -Destination $script:extension
        Copy-Item -Path (Join-Path $script:extensionSrc '*.cmd') `
            -Destination $script:extension
        Copy-Item -LiteralPath (Join-Path $script:extensionSrc 'bin') `
            -Destination $script:extension -Recurse

        $staging = & $script:simulator -BaseDir $script:base `
            -PublicSettings @{ skipCustomData = $true } -NoExecuteHandlers
        $bin = Join-Path $staging.HandlerRoot 'bin'

        $code = Invoke-HandlerOperation -Operation 'Enable' -ScriptRoot $bin
        $code | Should -Be 0

        $parsed = Get-Content -LiteralPath (Join-Path $staging.StatusFolder '0.status') -Raw |
            ConvertFrom-Json
        $parsed[0].status.status                 | Should -Be 'success'
        $parsed[0].status.formattedMessage.message | Should -Match 'skipped per settings'
        Should -Invoke -ModuleName $script:moduleName Start-Service -Exactly 0
    }

    It 'a thrown exception inside the lifecycle becomes a single error .status, exit 1' {
        Mock -ModuleName $script:moduleName Invoke-InstallOperation { throw 'simulated-handler-crash' }

        $code = Invoke-HandlerOperation -Operation 'Install' -ScriptRoot $script:handlerBin
        $code | Should -Be 1

        $parsed = Get-Content -LiteralPath (Join-Path $script:staging.StatusFolder '0.status') -Raw |
            ConvertFrom-Json
        $parsed[0].status.status                 | Should -Be 'error'
        $parsed[0].status.formattedMessage.message | Should -Be 'simulated-handler-crash'
    }
}
