#Requires -Version 7.0
#Requires -Modules @{ ModuleName='Pester'; ModuleVersion='5.5.0' }

# Unit tests for packaging/azure-extension/bin/HandlerLib.psm1.
#
# All system-mutating operations (sc.exe, Start-Service, Stop-Service, the
# egs-service.exe status probe) go through wrapper functions inside the
# module; we Mock those wrappers. The lifecycle code under test exercises:
#   - HandlerEnvironment.json discovery
#   - N.settings sequence selection
#   - Atomic .status file writing
#   - Install / Enable / Disable / Uninstall control flow
#   - Top-level dispatcher's transitioning->terminal status emission and
#     exception-to-error-status conversion.

BeforeAll {
    $script:ModulePath = (Resolve-Path "$PSScriptRoot/../../packaging/azure-extension/bin/HandlerLib.psm1").Path
    Import-Module $script:ModulePath -Force
    $script:ModuleName = 'HandlerLib'
}

AfterAll {
    Remove-Module -Name $script:ModuleName -Force -ErrorAction SilentlyContinue
}

Describe 'Get-HandlerEnvironment' {
    BeforeEach {
        $script:tmp = New-Item -ItemType Directory -Path (Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))) -Force
        $script:bin = New-Item -ItemType Directory -Path (Join-Path $tmp 'bin') -Force
    }

    It 'parses HandlerEnvironment.json placed one level above bin\' {
        $json = @(@{ version=1; handlerEnvironment=@{ logFolder='L'; configFolder='C'; statusFolder='S' } })
        $json | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $tmp 'HandlerEnvironment.json') -Encoding UTF8

        $env = Get-HandlerEnvironment -ScriptRoot $bin
        $env.logFolder    | Should -Be 'L'
        $env.configFolder | Should -Be 'C'
        $env.statusFolder | Should -Be 'S'
    }

    It 'also accepts HandlerEnvironment.json next to bin\Handler.ps1' {
        $json = @(@{ version=1; handlerEnvironment=@{ logFolder='LL'; configFolder='CC'; statusFolder='SS' } })
        $json | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $bin 'HandlerEnvironment.json') -Encoding UTF8

        $env = Get-HandlerEnvironment -ScriptRoot $bin
        $env.logFolder    | Should -Be 'LL'
    }

    It 'throws when HandlerEnvironment.json is missing' {
        { Get-HandlerEnvironment -ScriptRoot $bin } | Should -Throw -ExpectedMessage 'HandlerEnvironment.json not found*'
    }
}

Describe 'Get-LatestSettings' {
    BeforeEach {
        $script:cfg = New-Item -ItemType Directory -Path (Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))) -Force
        $script:env = [pscustomobject]@{ configFolder = $cfg.FullName }
    }

    It 'returns sequence 0 + empty when configFolder is missing' {
        $env = [pscustomobject]@{ configFolder = (Join-Path $TestDrive 'no-such-folder') }
        $result = Get-LatestSettings -HandlerEnvironment $env
        $result.Sequence | Should -Be 0
        $result.Public.Count | Should -Be 0
    }

    It 'returns sequence 0 + empty when no .settings file is present' {
        $result = Get-LatestSettings -HandlerEnvironment $script:env
        $result.Sequence | Should -Be 0
        $result.Public.Count | Should -Be 0
    }

    It 'picks the highest-numbered sequence file (numeric sort, not lexical)' {
        # '10' must beat '2' — lexical sort would put '2' first.
        foreach ($n in 0, 2, 10) {
            $payload = @(@{ runtimeSettings = @(@{ handlerSettings = @{ publicSettings = @{ seq = $n } } }) })
            $payload | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $cfg "$n.settings") -Encoding UTF8
        }
        $result = Get-LatestSettings -HandlerEnvironment $script:env
        $result.Sequence | Should -Be 10
        $result.Public.seq | Should -Be 10
    }

    It 'tolerates a settings file with null publicSettings' {
        $payload = @(@{ runtimeSettings = @(@{ handlerSettings = @{ publicSettings = $null } }) })
        $payload | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $cfg "5.settings") -Encoding UTF8
        $result = Get-LatestSettings -HandlerEnvironment $script:env
        $result.Sequence | Should -Be 5
        $result.Public.Count | Should -Be 0
    }
}

Describe 'Write-HandlerStatus' {
    BeforeEach {
        $script:statusFolder = New-Item -ItemType Directory -Path (Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))) -Force
        $script:env = [pscustomobject]@{ statusFolder = $statusFolder.FullName }
    }

    It 'writes a valid Azure VM Extension status payload' {
        Write-HandlerStatus -HandlerEnvironment $script:env -Sequence 3 -Operation 'Install' `
            -Status 'success' -Code 0 -Message 'Installed.'

        $path = Join-Path $script:statusFolder '3.status'
        Test-Path -LiteralPath $path | Should -BeTrue

        $parsed = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
        $parsed | Should -HaveCount 1
        $parsed[0].version | Should -Be '1.0'
        $parsed[0].status.name      | Should -Be 'Eryph.GuestServices'
        $parsed[0].status.operation | Should -Be 'Install'
        $parsed[0].status.status    | Should -Be 'success'
        $parsed[0].status.code      | Should -Be 0
        $parsed[0].status.formattedMessage.message | Should -Be 'Installed.'
    }

    It 'overwrites a prior .status file on the same sequence' {
        Write-HandlerStatus -HandlerEnvironment $script:env -Sequence 1 -Operation 'Install' `
            -Status 'transitioning' -Code 0 -Message 'starting'
        Write-HandlerStatus -HandlerEnvironment $script:env -Sequence 1 -Operation 'Install' `
            -Status 'success' -Code 0 -Message 'done'

        $parsed = Get-Content -LiteralPath (Join-Path $script:statusFolder '1.status') -Raw | ConvertFrom-Json
        $parsed[0].status.status | Should -Be 'success'
    }

    It 'creates statusFolder if it does not yet exist (agent occasionally lazy-creates)' {
        $newFolder = Join-Path $TestDrive 'late-status'
        $env = [pscustomobject]@{ statusFolder = $newFolder }
        Write-HandlerStatus -HandlerEnvironment $env -Sequence 0 -Operation 'Install' `
            -Status 'success' -Code 0 -Message 'ok'
        Test-Path -LiteralPath (Join-Path $newFolder '0.status') | Should -BeTrue
    }

    It 'rejects status values outside the Azure-documented set' {
        { Write-HandlerStatus -HandlerEnvironment $script:env -Sequence 0 -Operation 'Install' `
            -Status 'bogus' -Code 0 -Message 'x' } | Should -Throw
    }
}

Describe 'Get-EgsServiceName / Get-EgsInstallRoot / Get-EgsServiceBinary (env overrides)' {

    AfterEach {
        $env:EGS_HANDLER_SERVICE_NAME = $null
        $env:EGS_HANDLER_INSTALL_ROOT = $null
    }

    It 'returns production defaults when no env override is set' {
        $env:EGS_HANDLER_SERVICE_NAME = $null
        $env:EGS_HANDLER_INSTALL_ROOT = $null
        Get-EgsServiceName  | Should -Be 'eryph-guest-services'
        Get-EgsInstallRoot  | Should -Be 'C:\Program Files\eryph\guest-services'
        Get-EgsServiceBinary | Should -Be 'C:\Program Files\eryph\guest-services\bin\egs-service.exe'
    }

    It 'honours EGS_HANDLER_SERVICE_NAME and EGS_HANDLER_INSTALL_ROOT' {
        $env:EGS_HANDLER_SERVICE_NAME = 'eryph-guest-services-test'
        $env:EGS_HANDLER_INSTALL_ROOT = 'C:\Temp\egs-test'
        Get-EgsServiceName  | Should -Be 'eryph-guest-services-test'
        Get-EgsInstallRoot  | Should -Be 'C:\Temp\egs-test'
        Get-EgsServiceBinary | Should -Be 'C:\Temp\egs-test\bin\egs-service.exe'
    }
}

Describe 'Invoke-InstallOperation' {
    BeforeEach {
        $script:base = New-Item -ItemType Directory -Path (Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))) -Force
        $script:handler = New-Item -ItemType Directory -Path (Join-Path $base 'handler') -Force
        $script:bin = New-Item -ItemType Directory -Path (Join-Path $handler 'bin') -Force
        $script:payload = New-Item -ItemType Directory -Path (Join-Path $handler 'payload') -Force
        # A tiny payload tree to verify the recursive copy. Use a subfolder so
        # we know the recursion (-not just the root) is honoured.
        $payloadBin = New-Item -ItemType Directory -Path (Join-Path $payload 'bin') -Force
        Set-Content -LiteralPath (Join-Path $payloadBin 'egs-service.exe') -Value 'fake-exe'
        Set-Content -LiteralPath (Join-Path $payloadBin 'config.json') -Value '{}'

        $script:installRoot = Join-Path $base 'install'
        $env:EGS_HANDLER_INSTALL_ROOT = $script:installRoot
        $env:EGS_HANDLER_SERVICE_NAME = 'eryph-guest-services-test'

        # Default mock: service does not exist.
        Mock -ModuleName $script:ModuleName Get-Service { $null }
        Mock -ModuleName $script:ModuleName Stop-Service {}
        Mock -ModuleName $script:ModuleName New-EgsService {}
    }

    AfterEach {
        $env:EGS_HANDLER_INSTALL_ROOT = $null
        $env:EGS_HANDLER_SERVICE_NAME = $null
    }

    It 'copies payload/* to InstallRoot and registers the service' {
        Invoke-InstallOperation -ScriptRoot $script:bin

        Test-Path -LiteralPath (Join-Path $script:installRoot 'bin\egs-service.exe') | Should -BeTrue
        Test-Path -LiteralPath (Join-Path $script:installRoot 'bin\config.json') | Should -BeTrue

        Should -Invoke -ModuleName $script:ModuleName New-EgsService -Exactly 1 -ParameterFilter {
            $ServiceName -eq 'eryph-guest-services-test' -and
            $BinaryPath -like '*\bin\egs-service.exe'
        }
    }

    It 'wipes a prior InstallRoot before laying down the new payload' {
        New-Item -ItemType Directory -Path $script:installRoot -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $script:installRoot 'stale.txt') -Value 'leftover'

        Invoke-InstallOperation -ScriptRoot $script:bin

        Test-Path -LiteralPath (Join-Path $script:installRoot 'stale.txt') | Should -BeFalse
        Test-Path -LiteralPath (Join-Path $script:installRoot 'bin\egs-service.exe') | Should -BeTrue
    }

    It 'stops a running service before wiping InstallRoot' {
        New-Item -ItemType Directory -Path $script:installRoot -Force | Out-Null
        Mock -ModuleName $script:ModuleName Get-Service { [pscustomobject]@{ Name='eryph-guest-services-test' } }

        Invoke-InstallOperation -ScriptRoot $script:bin

        Should -Invoke -ModuleName $script:ModuleName Stop-Service -Exactly 1
    }

    It 'does not re-register the service if it already exists' {
        Mock -ModuleName $script:ModuleName Get-Service { [pscustomobject]@{ Name='eryph-guest-services-test' } }

        Invoke-InstallOperation -ScriptRoot $script:bin

        Should -Invoke -ModuleName $script:ModuleName New-EgsService -Exactly 0
    }

    It 'throws when the payload directory is missing' {
        Remove-Item -LiteralPath $script:payload -Recurse -Force
        { Invoke-InstallOperation -ScriptRoot $script:bin } |
            Should -Throw -ExpectedMessage '*payload missing*'
    }
}

Describe 'Invoke-EnableOperation' {
    BeforeEach {
        $env:EGS_HANDLER_SERVICE_NAME = 'eryph-guest-services-test'
        $env:EGS_HANDLER_INSTALL_ROOT = (Join-Path $TestDrive ([guid]::NewGuid().ToString('N')))

        Mock -ModuleName $script:ModuleName Get-Service { [pscustomobject]@{ Status = 'Running' } }
        Mock -ModuleName $script:ModuleName Start-Service {}
        Mock -ModuleName $script:ModuleName Start-Sleep {}
        Mock -ModuleName $script:ModuleName Invoke-EgsServiceStatus {
            @{ ExitCode = 0; Status = [pscustomobject]@{ state = 'completed' } }
        }
    }

    AfterEach {
        $env:EGS_HANDLER_SERVICE_NAME = $null
        $env:EGS_HANDLER_INSTALL_ROOT = $null
    }

    It 'short-circuits to success when skipCustomData is set' {
        $settings = @{ Sequence = 0; Public = [pscustomobject]@{ skipCustomData = $true } }
        $result = Invoke-EnableOperation -Settings $settings

        $result.Status | Should -Be 'success'
        $result.Message | Should -Match 'skipped per settings'
        Should -Invoke -ModuleName $script:ModuleName Start-Service -Exactly 0
        Should -Invoke -ModuleName $script:ModuleName Invoke-EgsServiceStatus -Exactly 0
    }

    It 'starts the service when not already running' {
        Mock -ModuleName $script:ModuleName Get-Service { [pscustomobject]@{ Status = 'Stopped' } }
        $settings = @{ Sequence = 0; Public = @{} }

        $result = Invoke-EnableOperation -Settings $settings -TimeoutMinutes 1 -PollIntervalSeconds 0

        $result.Status | Should -Be 'success'
        Should -Invoke -ModuleName $script:ModuleName Start-Service -Exactly 1
    }

    It 'returns success when the provisioning probe reports completed' {
        $settings = @{ Sequence = 0; Public = @{} }
        $result = Invoke-EnableOperation -Settings $settings -TimeoutMinutes 1 -PollIntervalSeconds 0
        $result.Status | Should -Be 'success'
        $result.Message | Should -Match 'Provisioning completed'
    }

    It 'returns error when the provisioning probe reports failed' {
        Mock -ModuleName $script:ModuleName Invoke-EgsServiceStatus {
            @{ ExitCode = 0; Status = [pscustomobject]@{ state = 'failed' } }
        }
        $settings = @{ Sequence = 0; Public = @{} }
        $result = Invoke-EnableOperation -Settings $settings -TimeoutMinutes 1 -PollIntervalSeconds 0
        $result.Status | Should -Be 'error'
        $result.Message | Should -Match 'Provisioning failed'
    }

    It 'times out (returns error) when the probe never reaches terminal' {
        Mock -ModuleName $script:ModuleName Invoke-EgsServiceStatus {
            @{ ExitCode = 0; Status = [pscustomobject]@{ state = 'running' } }
        }
        # Convince the loop the deadline has expired immediately by forcing
        # the (Get-Date) reading to advance past `now + TimeoutMinutes`.
        # Easiest: set TimeoutMinutes to 0 so the first deadline check
        # exits on the next iteration.
        $settings = @{ Sequence = 0; Public = @{} }
        $result = Invoke-EnableOperation -Settings $settings -TimeoutMinutes 0 -PollIntervalSeconds 0
        $result.Status | Should -Be 'error'
        $result.Message | Should -Match 'did not reach a terminal state'
    }

    It 'keeps polling when the probe is intermittent (exit code != 0)' {
        $script:probeCallCount = 0
        Mock -ModuleName $script:ModuleName Invoke-EgsServiceStatus {
            $script:probeCallCount++
            if ($script:probeCallCount -lt 3) {
                return @{ ExitCode = 1; Status = $null }
            }
            return @{ ExitCode = 0; Status = [pscustomobject]@{ state = 'completed' } }
        }

        $settings = @{ Sequence = 0; Public = @{} }
        $result = Invoke-EnableOperation -Settings $settings -TimeoutMinutes 5 -PollIntervalSeconds 0
        $result.Status | Should -Be 'success'
        $script:probeCallCount | Should -BeGreaterOrEqual 3
    }
}

Describe 'Invoke-DisableOperation' {
    BeforeEach {
        $env:EGS_HANDLER_SERVICE_NAME = 'eryph-guest-services-test'
        Mock -ModuleName $script:ModuleName Stop-Service {}
    }
    AfterEach { $env:EGS_HANDLER_SERVICE_NAME = $null }

    It 'stops a running service' {
        Mock -ModuleName $script:ModuleName Get-Service { [pscustomobject]@{ Status = 'Running' } }
        Invoke-DisableOperation
        Should -Invoke -ModuleName $script:ModuleName Stop-Service -Exactly 1
    }

    It 'does nothing when the service is already stopped' {
        Mock -ModuleName $script:ModuleName Get-Service { [pscustomobject]@{ Status = 'Stopped' } }
        Invoke-DisableOperation
        Should -Invoke -ModuleName $script:ModuleName Stop-Service -Exactly 0
    }

    It 'does nothing when the service is not registered' {
        Mock -ModuleName $script:ModuleName Get-Service { $null }
        Invoke-DisableOperation
        Should -Invoke -ModuleName $script:ModuleName Stop-Service -Exactly 0
    }
}

Describe 'Invoke-UninstallOperation' {
    BeforeEach {
        $script:base = New-Item -ItemType Directory -Path (Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))) -Force
        $script:installRoot = Join-Path $base 'install'
        New-Item -ItemType Directory -Path $installRoot -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $installRoot 'marker.txt') -Value 'x'

        $env:EGS_HANDLER_SERVICE_NAME = 'eryph-guest-services-test'
        $env:EGS_HANDLER_INSTALL_ROOT = $script:installRoot

        Mock -ModuleName $script:ModuleName Get-Service { [pscustomobject]@{ Status = 'Running' } }
        Mock -ModuleName $script:ModuleName Stop-Service {}
        Mock -ModuleName $script:ModuleName Remove-EgsService {}
    }

    AfterEach {
        $env:EGS_HANDLER_SERVICE_NAME = $null
        $env:EGS_HANDLER_INSTALL_ROOT = $null
    }

    It 'stops + removes the service and wipes InstallRoot' {
        Invoke-UninstallOperation

        Should -Invoke -ModuleName $script:ModuleName Stop-Service -Exactly 1
        Should -Invoke -ModuleName $script:ModuleName Remove-EgsService -Exactly 1
        Test-Path -LiteralPath $script:installRoot | Should -BeFalse
    }

    It 'tolerates missing service registration' {
        Mock -ModuleName $script:ModuleName Get-Service { $null }
        Invoke-UninstallOperation
        Should -Invoke -ModuleName $script:ModuleName Remove-EgsService -Exactly 0
        Test-Path -LiteralPath $script:installRoot | Should -BeFalse
    }

    It 'tolerates missing InstallRoot' {
        Remove-Item -LiteralPath $script:installRoot -Recurse -Force
        { Invoke-UninstallOperation } | Should -Not -Throw
    }
}

Describe 'Invoke-HandlerOperation (top-level dispatcher)' {
    BeforeEach {
        $script:base = New-Item -ItemType Directory -Path (Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))) -Force
        $script:bin = New-Item -ItemType Directory -Path (Join-Path $base 'bin') -Force
        # HandlerEnvironment.json one level above bin (canonical layout)
        $script:configFolder = New-Item -ItemType Directory -Path (Join-Path $base 'config') -Force
        $script:statusFolder = New-Item -ItemType Directory -Path (Join-Path $base 'status') -Force
        $script:logFolder    = New-Item -ItemType Directory -Path (Join-Path $base 'log') -Force

        $envPayload = @(@{ version=1; handlerEnvironment=@{
            logFolder    = $logFolder.FullName
            configFolder = $configFolder.FullName
            statusFolder = $statusFolder.FullName
        } })
        $envPayload | ConvertTo-Json -Depth 10 |
            Set-Content -LiteralPath (Join-Path $base 'HandlerEnvironment.json') -Encoding UTF8

        $settingsPayload = @(@{ runtimeSettings = @(@{ handlerSettings = @{ publicSettings = @{} } }) })
        $settingsPayload | ConvertTo-Json -Depth 10 |
            Set-Content -LiteralPath (Join-Path $configFolder '0.settings') -Encoding UTF8

        Mock -ModuleName $script:ModuleName Invoke-InstallOperation {}
        Mock -ModuleName $script:ModuleName Invoke-EnableOperation { @{ Status='success'; Message='ok' } }
        Mock -ModuleName $script:ModuleName Invoke-DisableOperation {}
        Mock -ModuleName $script:ModuleName Invoke-UninstallOperation {}
    }

    It 'writes a transitioning then success status for Install' {
        $code = Invoke-HandlerOperation -Operation 'Install' -ScriptRoot $script:bin
        $code | Should -Be 0

        $parsed = Get-Content -LiteralPath (Join-Path $script:statusFolder '0.status') -Raw | ConvertFrom-Json
        $parsed[0].status.operation | Should -Be 'Install'
        $parsed[0].status.status    | Should -Be 'success'
    }

    It 'returns the error code from Invoke-EnableOperation propagated as exit 1' {
        Mock -ModuleName $script:ModuleName Invoke-EnableOperation { @{ Status='error'; Message='boom' } }
        $code = Invoke-HandlerOperation -Operation 'Enable' -ScriptRoot $script:bin
        $code | Should -Be 1

        $parsed = Get-Content -LiteralPath (Join-Path $script:statusFolder '0.status') -Raw | ConvertFrom-Json
        $parsed[0].status.status                 | Should -Be 'error'
        $parsed[0].status.formattedMessage.message | Should -Be 'boom'
    }

    It 'catches a thrown exception and emits an error status' {
        Mock -ModuleName $script:ModuleName Invoke-InstallOperation { throw 'mock-install-failure' }
        $code = Invoke-HandlerOperation -Operation 'Install' -ScriptRoot $script:bin
        $code | Should -Be 1

        $parsed = Get-Content -LiteralPath (Join-Path $script:statusFolder '0.status') -Raw | ConvertFrom-Json
        $parsed[0].status.status                 | Should -Be 'error'
        $parsed[0].status.formattedMessage.message | Should -Be 'mock-install-failure'
    }

    It 'writes the .status file under the highest-numbered settings sequence' {
        # Drop a 7.settings so the dispatcher picks sequence 7.
        $settingsPayload = @(@{ runtimeSettings = @(@{ handlerSettings = @{ publicSettings = @{} } }) })
        $settingsPayload | ConvertTo-Json -Depth 10 |
            Set-Content -LiteralPath (Join-Path $script:configFolder '7.settings') -Encoding UTF8

        $code = Invoke-HandlerOperation -Operation 'Install' -ScriptRoot $script:bin
        $code | Should -Be 0
        Test-Path -LiteralPath (Join-Path $script:statusFolder '7.status') | Should -BeTrue
    }
}
