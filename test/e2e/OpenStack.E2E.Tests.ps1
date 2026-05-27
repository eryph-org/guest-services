#Requires -Version 7.4
#Requires -RunAsAdministrator

<#
.SYNOPSIS
  End-to-end test: the embedded provisioning agent provisions a Windows guest
  from a real OpenStack HTTP metadata service, exercising OpenStackMetadataDataSource.

.DESCRIPTION
  This proves the OpenStack metadata-service (HTTP) datasource end-to-end against
  real captured nova metadata, on eryph/Hyper-V:

  - Two overlay networks (sim-network.yaml): the guest sits on `default`, the
    simulator on `metadata` (pinned to 169.254.169.254 by a single-IP pool).
    eryph's virtual router connects them — reachability is already proven by
    probe-catlet.yaml.
  - The SIMULATOR catlet (Ubuntu) runs `egs-openstack-sim`, serving the captured
    config-2 tree (test/fixtures/configdrive-openstack) over HTTP with the
    /openstack version listing. The binary + fixture tree are uploaded via egs.
  - The GUEST catlet (Windows) is prepared offline before first boot:
      (1) egs-service binaries replaced with the local build; cloudbase-init
          disabled (Update-EgsServiceBinariesOffline).
      (2) SMBIOS chassis asset tag set to "OpenStack Nova" so the real
          ds_detect gate (IsRunningOnOpenStack) fires — Hyper-V can't set
          system-product-name, but the asset tag is accepted too.
      (3) egs-provisioning.json pins dataSources.dataSourceList to ["OpenStack"]
          so the locator probes only the metadata service (eryph's own config-2
          drive, ConfigDrive priority 40, would otherwise win over OpenStack 50).
  - On first boot egs-service detects OpenStack, fetches meta_data.json +
    user_data over HTTP from 169.254.169.254, and provisions. The test asserts
    the instance id and user-data came from the simulator.

.PREREQUISITES
  - Run as Administrator (Mount-VHD, offline reg load, Hyper-V WMI).
  - eryph-zero installed and `egs-tool initialize` completed.
  - Parent gene cached (dbosoft/<os>-standard/starter) and an Ubuntu 22.04 starter.
  - The .NET SDK on PATH (the harness publishes egs-openstack-sim for linux-x64).
#>
param (
    [Parameter()]
    [ValidateSet('winsrv2019', 'winsrv2022', 'winsrv2025')]
    [string] $OSVersion = 'winsrv2022',

    [Parameter()]
    [string] $PublishPath,

    [Parameter()]
    [string] $SimPublishPath
)

BeforeAll {
  $PSNativeCommandUseErrorActionPreference = $true
  $ErrorActionPreference = 'Stop'
  . $PSScriptRoot/Helpers.ps1

  # egs-service publish (the guest binaries we offline-install).
  if (-not $PublishPath) {
    $PublishPath = "$PSScriptRoot/../../src/Eryph.GuestServices.Service/bin/Release/net10.0/win-x64/publish"
  }
  $resolvedPublishPath = (Resolve-Path -LiteralPath $PublishPath).Path

  # egs-openstack-sim publish (linux-x64, self-contained) — published on demand.
  if (-not $SimPublishPath) {
    $simProject = "$PSScriptRoot/../Eryph.GuestServices.OpenStackMetadataSim/Eryph.GuestServices.OpenStackMetadataSim.csproj"
    $SimPublishPath = "$PSScriptRoot/../Eryph.GuestServices.OpenStackMetadataSim/bin/Release/net10.0/linux-x64/publish"
    if (-not (Test-Path -LiteralPath (Join-Path $SimPublishPath 'egs-openstack-sim'))) {
      Write-Host "Publishing egs-openstack-sim (linux-x64, self-contained) ..."
      dotnet publish $simProject -c Release -r linux-x64 --self-contained -o $SimPublishPath | Out-Host
    }
  }
  $resolvedSimPublishPath = (Resolve-Path -LiteralPath $SimPublishPath).Path

  # The captured, sanitized config-2 fixture the simulator serves.
  $fixtureTree = (Resolve-Path -LiteralPath "$PSScriptRoot/../fixtures/configdrive-openstack").Path
  # The sanitized identity the simulator's meta_data.json carries — the unique
  # proof that provisioning data came from the HTTP service (not eryph's drive).
  $script:expectedInstanceId = 'facade00-0000-4000-8000-000000000001'

  $sshPublicKey = (egs-tool get-ssh-key).Trim()
  if (-not $sshPublicKey) { throw "egs-tool get-ssh-key returned empty — run 'egs-tool initialize' first." }

  $project = New-TestProject
  Write-Host "Applying two-network config (default + metadata@169.254.169.254) to $($project.Name) ..."
  Set-VNetwork -ProjectName $project.Name -Config (Get-Content -Raw "$PSScriptRoot/openstack-sim-network.yaml") -Force

  # ---- Simulator catlet: serve the captured tree at 169.254.169.254 ----
  $simConfig = Get-Content -Raw "$PSScriptRoot/openstack-sim-catlet.yaml"
  $simName = New-CatletName
  Write-Host "Creating simulator catlet $simName ..."
  $sim = New-Catlet -Config $simConfig -Name $simName -ProjectName $project.Name `
    -Variables @{ sshPublicKey = $sshPublicKey } -SkipVariablesPrompt
  Start-Catlet -Id $sim.Id -Force

  $savedPref = $PSNativeCommandUseErrorActionPreference
  $PSNativeCommandUseErrorActionPreference = $false
  try {
    Write-Host "Waiting for simulator egs-service ..."
    Wait-Assert -Timeout (New-TimeSpan -Minutes 8) -Interval (New-TimeSpan -Seconds 5) {
      $status = egs-tool get-status $sim.VmId
      if ($status -ne 'available') { throw "sim guest services not available: $status" }
    }
    egs-tool update-ssh-config | Out-Null
    egs-tool add-ssh-config $sim.VmId | Out-Null
  }
  finally { $PSNativeCommandUseErrorActionPreference = $savedPref }

  $simHost = "$($sim.Id).eryph.alt"

  Write-Host "Uploading egs-openstack-sim + fixture tree to the simulator ..."
  egs-tool upload-directory $sim.VmId $resolvedSimPublishPath '/opt/egs-sim' --overwrite --recursive
  egs-tool upload-directory $sim.VmId $fixtureTree '/opt/egs-sim-data' --overwrite --recursive

  # Install + start the sim as a systemd service. Upload the setup script and run
  # it via `bash <file>` rather than passing a multi-line script as an ssh
  # argument — the egs SSH exec mangles complex quoted/piped command strings, and
  # simple single commands (`bash /path`) are reliable. Runs as root via egs.
  $simSetup = [System.IO.Path]::GetTempFileName()
  [System.IO.File]::WriteAllText($simSetup,
    ((Get-Content -Raw "$PSScriptRoot/openstack-sim-setup.sh") -replace "`r`n", "`n"))
  try {
    egs-tool upload-file $sim.VmId $simSetup '/opt/egs-sim/setup.sh' --overwrite
  } finally {
    Remove-Item -LiteralPath $simSetup -Force
  }
  # The egs proxy can drop a rapid reconnect; retry the one-shot setup a few times.
  Wait-Assert -Timeout (New-TimeSpan -Minutes 2) -Interval (New-TimeSpan -Seconds 5) {
    $r = ssh.exe -o StrictHostKeyChecking=no -o ConnectTimeout=20 $simHost 'bash /opt/egs-sim/setup.sh'
    if ($LASTEXITCODE -ne 0 -or "$r" -notmatch 'active') {
      throw "sim setup not active yet (exit=$LASTEXITCODE, out=$r)"
    }
  }

  Write-Host "Verifying the simulator serves the version listing ..."
  Wait-Assert -Timeout (New-TimeSpan -Minutes 2) -Interval (New-TimeSpan -Seconds 3) {
    $list = ssh.exe -o StrictHostKeyChecking=no $simHost 'curl -fsS http://localhost/openstack'
    if ($LASTEXITCODE -ne 0 -or "$list" -notmatch '2018-08-27') {
      throw "sim /openstack did not return the version listing (got: $list)"
    }
  }

  # ---- Guest-under-test: Windows, provisioned via the OpenStack HTTP source ----
  $guestConfig = (Get-Content -Raw "$PSScriptRoot/openstack-guest-catlet.yaml") `
    -replace '<<PARENT>>', "dbosoft/$OSVersion-standard/starter"
  $guestName = New-CatletName
  Write-Host "Creating guest catlet $guestName ..."
  $catlet = New-Catlet -Config $guestConfig -Name $guestName -ProjectName $project.Name `
    -Variables @{ sshPublicKey = $sshPublicKey } -SkipVariablesPrompt

  $state = (Get-VM -Id $catlet.VmId).State
  if ($state -ne 'Off') { throw "Expected guest catlet Off after creation; got $state." }

  Write-Host "Replacing egs-service binaries offline + disabling cloudbase-init ..."
  Update-EgsServiceBinariesOffline -VmId $catlet.VmId -PublishPath $resolvedPublishPath

  Write-Host "Setting SMBIOS chassis asset tag = 'OpenStack Nova' (trips ds_detect) ..."
  Set-CatletChassisAssetTag -VmId $catlet.VmId -AssetTag 'OpenStack Nova'

  Write-Host "Pinning datasource list to ['OpenStack'] (egs-provisioning.json) ..."
  Set-OfflineProvisioningSettings -VmId $catlet.VmId `
    -Settings @{ dataSources = @{ dataSourceList = @('OpenStack') } }

  Write-Host "Starting guest catlet $($catlet.Name) ..."
  Start-Catlet -Id $catlet.Id -Force

  # 169.254.169.254 is link-local; Windows treats it as on-link and never routes
  # it to eryph's virtual router. egs-service comes up regardless and its OpenStack
  # datasource WaitForReady-loops until the route lands, so install an onstart task
  # (re-runs across the SetHostnameModule reboot) that adds the active /32 route.
  Write-Host "Installing metadata-route task in the guest (link-local /32 -> overlay gateway) ..."
  Install-GuestMetadataRoute -VmId $catlet.VmId -CatletId $catlet.Id -MetadataIp '169.254.169.254'

  Write-Host "Waiting for provisioning to complete (KVP eryph.provisioning.state) ..."
  $finalState = Wait-ForProvisioningComplete -VmId $catlet.VmId -Timeout (New-TimeSpan -Minutes 15)
  Write-Host "Provisioning state: $finalState"

  # SSH alias for the assertions (egs over hvsocket), set up post-provisioning.
  $savedPref = $PSNativeCommandUseErrorActionPreference
  $PSNativeCommandUseErrorActionPreference = $false
  try {
    Wait-Assert -Timeout (New-TimeSpan -Minutes 5) -Interval (New-TimeSpan -Seconds 3) {
      $status = egs-tool get-status $catlet.VmId
      if ($status -ne 'available') { throw "guest services not available: $status" }
    }
    egs-tool update-ssh-config | Out-Null
    egs-tool add-ssh-config $catlet.VmId | Out-Null
    for ($i = 0; $i -lt 40; $i++) {
      ssh.exe -o StrictHostKeyChecking=no -o ConnectTimeout=5 "$($catlet.Id).eryph.alt" 'hostname' 2>$null | Out-Null
      if ($LASTEXITCODE -eq 0) { break }
      Start-Sleep -Seconds 3
    }
  }
  finally { $PSNativeCommandUseErrorActionPreference = $savedPref }

  function script:Invoke-GuestPS {
    param([string] $HostName, [string] $Script)
    $output = ssh.exe -o StrictHostKeyChecking=no $HostName "powershell -NoProfile -Command `"$Script`""
    return @{ ExitCode = $LASTEXITCODE; Output = ($output | Out-String).Trim() }
  }
}

AfterAll {
  . $PSScriptRoot/Helpers.ps1

  if ($catlet) {
    try {
      Save-GuestDiagnostics -CatletId $catlet.Id `
        -OutputDir (Join-Path $PSScriptRoot "diagnostics/$($catlet.Name)")
    }
    catch { Write-Host "Guest diagnostics collection failed (non-fatal): $_" }
  }

  if ($env:EGS_E2E_KEEP_VM) {
    Write-Host "EGS_E2E_KEEP_VM set — leaving catlets for inspection."
    return
  }
  if ($catlet) { Remove-Catlet -Id $catlet.Id -Force -ErrorAction SilentlyContinue }
  if ($sim) { Remove-Catlet -Id $sim.Id -Force -ErrorAction SilentlyContinue }
  if ($project) { Remove-EryphProject -Id $project.Id -Force -ErrorAction SilentlyContinue }
}

Describe 'OpenStack metadata-service provisioning at first boot' {

  Context 'lifecycle' {

    It 'reports completed via KVP' {
      $kvp = egs-tool get-data --json $catlet.VmId | ConvertFrom-Json -AsHashtable
      $kvp.guest.'eryph.provisioning.state' | Should -Be 'completed'
      $kvp.guest.ContainsKey('eryph.provisioning.error') | Should -BeFalse
    }

    It 'provisioned the instance id served by the simulator (proves the HTTP metadata source won)' {
      # Only the simulator's meta_data.json carries this uuid; eryph's own
      # config-2 drive would have a different instance id. So this is the
      # discriminating proof the OpenStack HTTP datasource supplied the data.
      $kvp = egs-tool get-data --json $catlet.VmId | ConvertFrom-Json -AsHashtable
      $kvp.guest.'eryph.provisioning.instance' | Should -Be $expectedInstanceId
    }

    It 'state.json records the simulator instance id and reached Final' {
      $hostName = "$($catlet.Id).eryph.alt"
      $r = Invoke-GuestPS -HostName $hostName `
        -Script 'Get-Content -Raw -LiteralPath C:\ProgramData\eryph\provisioning\state.json'
      $r.ExitCode | Should -Be 0
      $state = $r.Output | ConvertFrom-Json -AsHashtable
      $state.instanceId | Should -Be $expectedInstanceId
      $state.completedStages | Should -Contain 'Final'
    }
  }

  Context 'user-data from the metadata service was applied' {

    It 'WriteFilesModule wrote the file from the simulator user_data' {
      # The simulator's user_data is "#cloud-config / write_files:
      # C:\eryph-openstack-e2e\hello.txt = from-userdata". Its presence proves
      # user_data flowed from the HTTP metadata service and was applied.
      $hostName = "$($catlet.Id).eryph.alt"
      $r = Invoke-GuestPS -HostName $hostName `
        -Script 'Get-Content -Raw -LiteralPath C:\eryph-openstack-e2e\hello.txt'
      $r.ExitCode | Should -Be 0
      $r.Output.Trim() | Should -Be 'from-userdata'
    }
  }

  Context 'a login user from the metadata service was provisioned' {
    # The discriminating "technically working" proof: the OpenStack user-data
    # declares `users: [osadmin]` with a password. The datasource must deliver
    # it and the agent must create a usable login — not just write files.

    It 'created osadmin in Administrators, enabled' {
      # Single boolean expression (no ';' — a literal semicolon in the script
      # string is parsed as a statement separator by the powershell -Command
      # wrapper and splits the output). True only if osadmin is enabled AND an
      # Administrators member.
      $hostName = "$($catlet.Id).eryph.alt"
      $r = Invoke-GuestPS -HostName $hostName `
        -Script "[bool](Get-LocalUser osadmin -ErrorAction SilentlyContinue).Enabled -and [bool](Get-LocalGroupMember Administrators -Member osadmin -ErrorAction SilentlyContinue)"
      $r.ExitCode | Should -Be 0
      $r.Output.Trim() | Should -Be 'True'
    }

    It 'provisioned osadmin with a WORKING password (can authenticate)' {
      # ValidateCredentials returns true only when the password matches AND the
      # account is enabled, not locked, and not expired — the real "can log in"
      # proof, same bar as the Provisioning suite.
      $hostName = "$($catlet.Id).eryph.alt"
      $r = Invoke-GuestPS -HostName $hostName `
        -Script "Add-Type -AssemblyName System.DirectoryServices.AccountManagement; (New-Object System.DirectoryServices.AccountManagement.PrincipalContext('Machine')).ValidateCredentials('osadmin','XyzqW3lc0me!2!')"
      $r.ExitCode | Should -Be 0
      $r.Output.Trim() | Should -Be 'True'
    }
  }

  Context 'CLI on the guest' {

    It 'egs-service status --json reports Final completed' {
      $hostName = "$($catlet.Id).eryph.alt"
      $r = Invoke-GuestPS -HostName $hostName `
        -Script "& 'C:\Program Files\eryph\guest-services\bin\egs-service.exe' status --json"
      $r.ExitCode | Should -Be 0
      ($r.Output | ConvertFrom-Json -AsHashtable).completedStages | Should -Contain 'Final'
    }
  }
}
