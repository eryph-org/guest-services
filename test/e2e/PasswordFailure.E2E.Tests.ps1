#Requires -Version 7.4
#Requires -RunAsAdministrator

<#
.SYNOPSIS
  End-to-end test: an invalid user password fails provisioning, and the failure
  is surfaced in the provisioning status (KVP) and the agent log — not swallowed
  or reported as success.

.DESCRIPTION
  The catlet config (`password-failure-catlet.yaml`) asks for a local user
  `admin` with the password `admin`. The eryph base image enforces the Windows
  password-complexity policy, which rejects that password (too short, and it
  contains the account name) with NERR_PasswordTooShort (2245). UsersGroupsModule
  throws while setting it, the Config stage fails, and provisioning reports
  `failed`.

  Same offline-install mechanics as Provisioning.E2E.Tests.ps1: mount the VHD
  before first boot, copy our egs-service binaries in, disable cloudbase-init if
  present, boot, and let the embedded agent provision. Unlike the happy-path
  suites, this one waits for provisioning to reach a terminal `failed` state
  (Wait-ForProvisioningComplete would throw on `failed`, so a small inline poll
  is used) and then asserts on the KVP status, the cloud-init FAIL event, and the
  agent log — none of which need an in-guest SSH session, only the egs channel.

.PREREQUISITES
  - Run as Administrator (Mount-VHD + offline reg load).
  - Hyper-V management cmdlets available.
  - eryph-zero installed and `egs-tool initialize` completed.
  - Parent gene cached (dbosoft/winsrv2022-standard/starter by default).
#>
param (
    [Parameter()]
    [ValidateSet('winsrv2019', 'winsrv2022', 'winsrv2025')]
    [string] $OSVersion = 'winsrv2022',

    [Parameter()]
    [string] $PublishPath
)

BeforeAll {
  $PSNativeCommandUseErrorActionPreference = $true
  $ErrorActionPreference = 'Stop'
  . $PSScriptRoot/Helpers.ps1

  if (-not $PublishPath) {
    $PublishPath = "$PSScriptRoot/../../src/Eryph.GuestServices.Service/bin/Release/net10.0/win-x64/publish"
  }
  $resolvedPublishPath = (Resolve-Path -LiteralPath $PublishPath).Path

  $catletConfig = (Get-Content -Raw -Path $PSScriptRoot/password-failure-catlet.yaml) `
    -replace '<<PARENT>>', "dbosoft/$OSVersion-standard/starter"

  $project = New-TestProject
  $catletName = New-CatletName
  Write-Host "Creating BAD-PASSWORD catlet $catletName (user admin / passwd admin) ..."
  $catlet = New-Catlet -Config $catletConfig -Name $catletName -ProjectName $project.Name

  $state = (Get-VM -Id $catlet.VmId).State
  if ($state -ne 'Off') { throw "Expected catlet to be Off after creation; got $state." }

  Write-Host "Replacing egs-service binaries offline (publish=$resolvedPublishPath) ..."
  Update-EgsServiceBinariesOffline -VmId $catlet.VmId -PublishPath $resolvedPublishPath

  Write-Host "Starting catlet $($catlet.Name) ..."
  Start-Catlet -Id $catlet.Id -Force

  # Wait for provisioning to reach a TERMINAL state. Wait-ForProvisioningComplete
  # throws on `failed`, which is the expected outcome here, so poll directly and
  # capture whichever terminal state is reached ('failed' expected, 'completed'
  # would mean the base image did not reject the password).
  Write-Host "Waiting for provisioning to reach a terminal state (KVP eryph.provisioning.state) ..."
  $savedPref = $PSNativeCommandUseErrorActionPreference
  $PSNativeCommandUseErrorActionPreference = $false
  try {
    $deadline = (Get-Date).Add((New-TimeSpan -Minutes 12))
    $script:finalState = $null
    while ((Get-Date) -lt $deadline) {
      try {
        $kvp = egs-tool get-data --json $catlet.VmId | ConvertFrom-Json -AsHashtable
        $s = $kvp.guest.'eryph.provisioning.state'
        if ($s -eq 'failed' -or $s -eq 'completed') { $script:finalState = $s; break }
        Write-Verbose "provisioning state=$s — waiting..."
      } catch { Write-Verbose "KVP not yet readable: $_" }
      Start-Sleep -Seconds 5
    }
    Write-Host "Terminal provisioning state: $script:finalState"

    # egs-service is up regardless of the provisioning outcome; wait for it so the
    # agent-log download below (and the KVP read in the tests) go over a ready
    # channel.
    Wait-Assert -Timeout (New-TimeSpan -Minutes 5) -Interval (New-TimeSpan -Seconds 3) {
      $status = egs-tool get-status $catlet.VmId
      if ($status -ne 'available') { throw "guest services not available: $status" }
    }

    # Snapshot the KVP surface and pull the agent log once, so each test reads
    # the same captured state without re-fetching. The egs file-transfer channel
    # can lag get-status=available by a few seconds, so retry the download until
    # the file lands rather than racing it.
    $script:kvp = egs-tool get-data --json $catlet.VmId | ConvertFrom-Json -AsHashtable

    # Register this client's key with the guest so the egs file-transfer channel
    # authorizes the download below. (Save-GuestDiagnostics does the same before
    # its own download-file calls.)
    egs-tool add-ssh-config $catlet.VmId | Out-Null

    $script:agentLog = Join-Path ([System.IO.Path]::GetTempPath()) "egs-pwfail-$($catlet.VmId).log"
    Remove-Item $script:agentLog -ErrorAction SilentlyContinue
    for ($i = 0; $i -lt 12 -and -not (Test-Path $script:agentLog); $i++) {
      egs-tool download-file $catlet.VmId 'C:\ProgramData\eryph\guest-services\logs\agent.log' $script:agentLog 2>$null
      if (-not (Test-Path $script:agentLog)) { Start-Sleep -Seconds 3 }
    }
  }
  finally { $PSNativeCommandUseErrorActionPreference = $savedPref }
}

AfterAll {
  . $PSScriptRoot/Helpers.ps1
  if ($catlet) {
    try {
      Save-GuestDiagnostics -CatletId $catlet.Id `
        -OutputDir (Join-Path $PSScriptRoot "diagnostics/$($catlet.Name)")
    } catch { Write-Host "Guest diagnostics collection failed (non-fatal): $_" }
  }
  if ($env:EGS_E2E_KEEP_VM) {
    Write-Host "EGS_E2E_KEEP_VM set — leaving catlet $($catlet.Name) for inspection."
    return
  }
  if ($catlet) { Remove-Catlet -Id $catlet.Id -Force -ErrorAction SilentlyContinue }
  if ($project) { Remove-EryphProject -Id $project.Id -Force -ErrorAction SilentlyContinue }
}

Describe 'Provisioning fails on an invalid user password' {

  Context 'provisioning status' {

    It 'reports state = failed via KVP (not completed)' {
      # The invalid password must flip the single status key to 'failed'. A
      # 'completed' here would mean the base image accepted admin/admin, which
      # would invalidate the premise of this suite.
      $script:finalState | Should -Be 'failed'
      $script:kvp.guest.'eryph.provisioning.state' | Should -Be 'failed'
    }

    It 'surfaces the failure in the cloud-init event stream (modules-config FAIL)' {
      # RFC 0031 Surface 1: the failure reason rides the CLOUD_INIT|... stream,
      # the same way real cloud-init reports it on Linux. The users/groups module
      # runs in the Config stage, so the terminal event is a `finish` for
      # `modules-config` (and its readable `modules-config/users-groups` child)
      # with result FAIL, and its msg carries the password-policy error.
      $cloudInitKeys = $script:kvp.guest.Keys | Where-Object { $_ -like 'CLOUD_INIT|*' }
      $cloudInitKeys | Should -Not -BeNullOrEmpty

      $configFinishKeys = $cloudInitKeys | Where-Object { $_ -like 'CLOUD_INIT|*|finish|modules-config*' }
      $failEvents = $configFinishKeys |
        ForEach-Object { $script:kvp.guest[$_] } |
        Where-Object { $_.result -eq 'FAIL' }
      $failEvents | Should -Not -BeNullOrEmpty `
        -Because 'the failed Config stage must emit a FAIL finish event'

      # The module is reported by its readable name (users-groups), NOT the C#
      # type name (UsersGroupsModule).
      ($configFinishKeys | Where-Object { $_ -like '*modules-config/users-groups|*' }) |
        Should -Not -BeNullOrEmpty -Because 'the module must appear under its readable report name'
      ($cloudInitKeys | Where-Object { $_ -like '*UsersGroupsModule*' }) |
        Should -BeNullOrEmpty -Because 'the report must not leak the C# type name'

      # At least one FAIL event's message must name the offending user and the
      # human-readable policy reason.
      ($failEvents | Where-Object { $_.msg -match "admin" -and $_.msg -match "does not meet the system" }) |
        Should -Not -BeNullOrEmpty `
        -Because 'the FAIL reason must identify the invalid admin password in readable terms'
    }
  }

  Context 'provisioning logs' {

    It 'the agent log reports the invalid password for the admin user' {
      # The provisioning log (agent.log) must record why the run failed: the
      # module threw while applying the admin password, and the error is a
      # human-readable password-policy rejection (not a raw NetUserSetInfo code).
      # Matched as booleans via Select-String -Quiet so no log content is echoed.
      Test-Path $script:agentLog | Should -BeTrue

      # StageRunner logs the failing module by its C# type for a precise
      # diagnostic (the readable name is what goes to the KVP/cloud-init report).
      $moduleThrew = [bool](Select-String -LiteralPath $script:agentLog `
        -Pattern 'UsersGroupsModule threw' -Quiet)
      $passwordError = [bool](Select-String -LiteralPath $script:agentLog `
        -Pattern "set the password for user 'admin'" -Quiet)
      $readableReason = [bool](Select-String -LiteralPath $script:agentLog `
        -Pattern 'does not meet the system' -Quiet)

      $moduleThrew | Should -BeTrue -Because 'the users/groups module failure must be logged'
      $passwordError | Should -BeTrue -Because 'the failed password operation must name the admin user'
      $readableReason | Should -BeTrue -Because 'the failure reason must be human-readable, not a raw Win32 code'
    }

    It 'did not log the admin user as successfully provisioned' {
      # Guard against a regression where the password failure is swallowed and
      # the run reports success anyway: the completed-provisioning marker must
      # not be present.
      [bool](Select-String -LiteralPath $script:agentLog `
        -Pattern 'Provisioning completed for instance' -Quiet) |
        Should -BeFalse -Because 'a run that failed on the password must not report completion'
    }
  }
}
