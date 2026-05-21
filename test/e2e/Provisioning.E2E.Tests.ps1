#Requires -Version 7.4
#Requires -RunAsAdministrator

<#
.SYNOPSIS
  End-to-end test: the embedded provisioning agent runs at first boot via
  the egs-service Windows service, with cloudbase-init explicitly disabled.

.DESCRIPTION
  This suite is structurally different from Shell.E2E.Tests.ps1:

  - Catlet is created from a MINIMAL config (no fodder, no SSH-key injection
    via the dbosoft/guest-services gene). The parent gene gives us Windows +
    pre-installed cloudbase-init; nothing else.
  - Before first start we mount the catlet's VHD, copy our locally-built
    egs-service binaries into C:\Program Files\eryph\guest-services\bin\,
    register egs-service as an automatic Windows service via the offline
    SYSTEM hive, and disable cloudbase-init by renaming its install dir AND
    setting its service Start=Disabled.
  - On first boot, only egs-service runs. Its ProvisioningHostedService
    fires once, discovers a datasource (or NoDataSource), and reports state
    via KVP. We assert on KVP and on the state file inside the VM.

  Why this matters: it's the cleanest possible test of the embedded
  provisioning lifecycle — first SCM start, fresh state, no cbi to fight,
  no post-boot patch dance.

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

  $catletConfigTemplate = Get-Content -Raw -Path $PSScriptRoot/provisioning-catlet.yaml
  $catletConfig = $catletConfigTemplate `
    -replace '<<PARENT>>', "dbosoft/$OSVersion-standard/starter"

  $project = New-TestProject
  $catletName = New-CatletName
  Write-Host "Creating BASE catlet $catletName in project $($project.Name) ..."
  $catlet = New-Catlet -Config $catletConfig -Name $catletName `
    -ProjectName $project.Name -SkipVariablesPrompt

  # The catlet must remain Stopped so we can mount its VHD. New-Catlet
  # creates but does not start by default — confirm.
  $state = (Get-VM -Id $catlet.VmId).State
  if ($state -ne 'Off') {
    throw "Expected catlet to be Off after creation; got $state."
  }

  Write-Host "Baking egs-service offline (publish=$resolvedPublishPath) ..."
  Install-EgsServiceOffline -VmId $catlet.VmId -PublishPath $resolvedPublishPath

  Write-Host "Starting catlet $($catlet.Name) ..."
  Start-Catlet -Id $catlet.Id -Force

  egs-tool update-ssh-config
  egs-tool add-ssh-config $catlet.VmId

  Write-Host "Waiting for provisioning to complete (KVP eryph.provisioning.state) ..."
  $finalState = Wait-ForProvisioningComplete -VmId $catlet.VmId `
    -Timeout (New-TimeSpan -Minutes 10)
  Write-Host "Provisioning state: $finalState"
}

AfterAll {
  if ($env:EGS_E2E_KEEP_VM) {
    Write-Host "EGS_E2E_KEEP_VM set — leaving catlet $($catlet.Name) for inspection."
    return
  }
  if ($catlet) {
    Remove-Catlet -Id $catlet.Id -Force -ErrorAction SilentlyContinue
  }
  if ($project) {
    Remove-EryphProject -Id $project.Id -Force -ErrorAction SilentlyContinue
  }
}

Describe 'Embedded provisioning at first boot' {

  It 'reports completed via KVP' {
    $kvp = egs-tool get-data --json $catlet.VmId | ConvertFrom-Json -AsHashtable
    $kvp.guest.'eryph.provisioning.state' | Should -Be 'completed'
    $kvp.guest.ContainsKey('eryph.provisioning.error') | Should -BeFalse
  }

  It 'reports a non-empty instance id via KVP' {
    $kvp = egs-tool get-data --json $catlet.VmId | ConvertFrom-Json -AsHashtable
    $kvp.guest.'eryph.provisioning.instance' | Should -Not -BeNullOrEmpty
  }

  It 'wrote the state file inside the guest' {
    $hostName = "$($catlet.Id).eryph.alt"
    $output = ssh.exe -o StrictHostKeyChecking=no $hostName `
      'powershell -NoProfile -Command "Get-Content -Raw -LiteralPath C:\ProgramData\eryph\provisioning\state.json"'
    $LASTEXITCODE | Should -Be 0
    $state = $output | ConvertFrom-Json -AsHashtable
    $state.instanceId | Should -Not -BeNullOrEmpty
    $state.completedStages | Should -Contain 'Final'
  }

  It 'is running the patched binary (egs-service version)' {
    $hostName = "$($catlet.Id).eryph.alt"
    $hostBuildVersion = (Get-Item "$resolvedPublishPath\egs-service.exe").VersionInfo.FileVersion
    $guestVersion = (ssh.exe -o StrictHostKeyChecking=no $hostName `
      '"C:\Program Files\eryph\guest-services\bin\egs-service.exe" version').Trim()
    # The CLI prints "egs-service <FileVersion> ..." — accept any line that
    # contains the host's build version string.
    $guestVersion | Should -Match ([regex]::Escape($hostBuildVersion))
  }

  It 'left no cloudbase-init service running' {
    $hostName = "$($catlet.Id).eryph.alt"
    $svc = ssh.exe -o StrictHostKeyChecking=no $hostName `
      'powershell -NoProfile -Command "(Get-Service cloudbase-init -ErrorAction SilentlyContinue).Status"'
    # Either the service was renamed away (no Status) or its StartType=Disabled
    # and the Status is Stopped. Anything Running here is a bug.
    if ($svc) { $svc.Trim() | Should -Not -Be 'Running' }
  }

  It 'egs-service status --json reports Final completed' {
    $hostName = "$($catlet.Id).eryph.alt"
    $json = ssh.exe -o StrictHostKeyChecking=no $hostName `
      '"C:\Program Files\eryph\guest-services\bin\egs-service.exe" status --json'
    $LASTEXITCODE | Should -Be 0
    $obj = $json | ConvertFrom-Json -AsHashtable
    $obj.completedStages | Should -Contain 'Final'
  }
}
