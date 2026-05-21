#Requires -Version 7.4
#Requires -RunAsAdministrator

<#
.SYNOPSIS
  End-to-end test: the embedded provisioning agent runs at first boot via
  the egs-service Windows service, with cloudbase-init explicitly disabled.

.DESCRIPTION
  This suite is structurally different from Shell.E2E.Tests.ps1:

  - The catlet config (`provisioning-catlet.yaml`) is the TEST INPUT — its
    cloud-config fodder is what our agent is asked to process. eryph-zero
    compiles fodder into a ConfigDrive ISO that gets attached to the catlet.
  - We do NOT add the dbosoft/guest-services gene; the parent gives us
    Windows + cloudbase-init. We install our agent offline via VHD mount.
  - Before first start we mount the catlet's VHD, copy our locally-built
    egs-service binaries into C:\Program Files\eryph\guest-services\bin\,
    register egs-service as an automatic Windows service via the offline
    SYSTEM hive, and disable cloudbase-init by renaming its install dir AND
    setting its service Start=Disabled.
  - On first boot, only egs-service runs. Its ProvisioningHostedService
    discovers the ConfigDrive datasource, processes our cloud-config, and
    reports completion via KVP. SetHostnameModule may trigger a reboot;
    Wait-ForProvisioningComplete polls through that.
  - Tests assert the configured outcomes: hostname is set, user was created,
    write_files markers exist (both plain and base64-decoded), runcmd ran.

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

  $sshPublicKey = (egs-tool get-ssh-key).Trim()
  if (-not $sshPublicKey) { throw "egs-tool get-ssh-key returned empty — run 'egs-tool initialize' first." }

  $catletConfigTemplate = Get-Content -Raw -Path $PSScriptRoot/provisioning-catlet.yaml
  $catletConfig = $catletConfigTemplate `
    -replace '<<PARENT>>', "dbosoft/$OSVersion-standard/starter"

  $project = New-TestProject
  $catletName = New-CatletName
  Write-Host "Creating BASE catlet $catletName in project $($project.Name) ..."
  # The cloud-config fodder uses {{ sshPublicKey }}; eryph-zero substitutes
  # it during compile. -Variables binds the value (no interactive prompt).
  $catlet = New-Catlet -Config $catletConfig -Name $catletName `
    -ProjectName $project.Name `
    -Variables @{ sshPublicKey = $sshPublicKey } `
    -SkipVariablesPrompt

  # The catlet must remain Stopped so we can mount its VHD. New-Catlet
  # creates but does not start by default — confirm.
  $state = (Get-VM -Id $catlet.VmId).State
  if ($state -ne 'Off') {
    throw "Expected catlet to be Off after creation; got $state."
  }

  Write-Host "Replacing egs-service binaries offline (publish=$resolvedPublishPath) ..."
  Update-EgsServiceBinariesOffline -VmId $catlet.VmId -PublishPath $resolvedPublishPath

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

function Invoke-GuestPS {
  param([string] $HostName, [string] $Script)
  $output = ssh.exe -o StrictHostKeyChecking=no $HostName "powershell -NoProfile -Command `"$Script`""
  return @{
    ExitCode = $LASTEXITCODE
    Output   = ($output | Out-String).Trim()
  }
}

Describe 'Embedded provisioning at first boot' {

  Context 'lifecycle' {

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
      $r = Invoke-GuestPS -HostName $hostName `
        -Script 'Get-Content -Raw -LiteralPath C:\ProgramData\eryph\provisioning\state.json'
      $r.ExitCode | Should -Be 0
      $state = $r.Output | ConvertFrom-Json -AsHashtable
      $state.instanceId | Should -Not -BeNullOrEmpty
      $state.completedStages | Should -Contain 'Final'
    }

    It 'is running the patched binary (egs-service version matches host build)' {
      $hostName = "$($catlet.Id).eryph.alt"
      $hostBuildVersion = (Get-Item "$resolvedPublishPath\egs-service.exe").VersionInfo.FileVersion
      $guestVersion = (ssh.exe -o StrictHostKeyChecking=no $hostName `
        '"C:\Program Files\eryph\guest-services\bin\egs-service.exe" version').Trim()
      $guestVersion | Should -Match ([regex]::Escape($hostBuildVersion))
    }

    It 'did not run cloudbase-init' {
      $hostName = "$($catlet.Id).eryph.alt"
      $r = Invoke-GuestPS -HostName $hostName `
        -Script '(Get-Service cloudbase-init -ErrorAction SilentlyContinue).Status'
      if ($r.Output) { $r.Output | Should -Not -Be 'Running' }
    }
  }

  Context 'cloud-config payload was applied' {

    It 'SetHostnameModule set the computer name to egs-prov-e2e' {
      $hostName = "$($catlet.Id).eryph.alt"
      $r = Invoke-GuestPS -HostName $hostName -Script '$env:COMPUTERNAME'
      $r.ExitCode | Should -Be 0
      $r.Output | Should -Be 'egs-prov-e2e'
    }

    It 'UsersGroupsModule created prov_test_user in Administrators' {
      $hostName = "$($catlet.Id).eryph.alt"
      $r = Invoke-GuestPS -HostName $hostName `
        -Script '(Get-LocalGroupMember -Group Administrators -Member prov_test_user -ErrorAction SilentlyContinue).Name'
      $r.ExitCode | Should -Be 0
      $r.Output | Should -Match 'prov_test_user'
    }

    It 'WriteFilesModule produced the plain marker file' {
      $hostName = "$($catlet.Id).eryph.alt"
      $r = Invoke-GuestPS -HostName $hostName `
        -Script 'Get-Content -Raw -LiteralPath C:\ProgramData\eryph-e2e\marker-plain.txt'
      $r.ExitCode | Should -Be 0
      $r.Output.Trim() | Should -Be 'marker-plain'
    }

    It 'WriteFilesModule decoded the base64 marker file' {
      $hostName = "$($catlet.Id).eryph.alt"
      $r = Invoke-GuestPS -HostName $hostName `
        -Script 'Get-Content -Raw -LiteralPath C:\ProgramData\eryph-e2e\marker-b64.txt'
      $r.ExitCode | Should -Be 0
      $r.Output.Trim() | Should -Be 'marker-b64'
    }

    It 'RuncmdModule executed the runcmd entry' {
      $hostName = "$($catlet.Id).eryph.alt"
      $r = Invoke-GuestPS -HostName $hostName `
        -Script 'Get-Content -Raw -LiteralPath C:\ProgramData\eryph-e2e\runcmd-marker.txt'
      $r.ExitCode | Should -Be 0
      $r.Output.Trim() | Should -Be 'runcmd-ran'
    }
  }

  Context 'CLI on the guest' {

    It 'egs-service status --json reports Final completed' {
      $hostName = "$($catlet.Id).eryph.alt"
      $json = ssh.exe -o StrictHostKeyChecking=no $hostName `
        '"C:\Program Files\eryph\guest-services\bin\egs-service.exe" status --json'
      $LASTEXITCODE | Should -Be 0
      ($json | ConvertFrom-Json -AsHashtable).completedStages | Should -Contain 'Final'
    }
  }
}
