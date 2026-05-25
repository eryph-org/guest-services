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

  # Record the OS partition size while the catlet is still offline. The
  # growpart Context compares this against the in-guest partition size
  # after first boot to prove the GrowpartModule actually grew it (and not
  # that the parent gene happens to ship a large partition).
  Write-Host "Recording offline OS partition size ..."
  $script:offlineOsPartitionSize = Get-CatletOsPartitionSize -VmId $catlet.VmId
  Write-Host "Offline OS partition size: $([math]::Round($offlineOsPartitionSize / 1GB, 2)) GB"

  Write-Host "Starting catlet $($catlet.Name) ..."
  Start-Catlet -Id $catlet.Id -Force

  Write-Host "Waiting for provisioning to complete (KVP eryph.provisioning.state) ..."
  $finalState = Wait-ForProvisioningComplete -VmId $catlet.VmId `
    -Timeout (New-TimeSpan -Minutes 10)
  Write-Host "Provisioning state: $finalState"

  # Set up the SSH alias AFTER provisioning is done — egs-tool needs the
  # post-reboot egs-service to be listening so it can resolve the catlet's
  # Hyper-V socket. Calling earlier (or only once) produces stale aliases
  # whose HostName never resolves.
  Write-Host "Waiting for egs-service SSH listener (get-status=available) ..."
  $savedPref = $PSNativeCommandUseErrorActionPreference
  $PSNativeCommandUseErrorActionPreference = $false
  try {
    Wait-Assert -Timeout (New-TimeSpan -Minutes 5) -Interval (New-TimeSpan -Seconds 3) {
      $status = egs-tool get-status $catlet.VmId
      if ($status -ne 'available') { throw "guest services not available: $status" }
    }
    Write-Host "egs-service is available — refreshing SSH config aliases ..."
    egs-tool update-ssh-config | Out-Null
    egs-tool add-ssh-config $catlet.VmId | Out-Null
  }
  finally {
    $PSNativeCommandUseErrorActionPreference = $savedPref
  }

  # Pester 5 isolates each Describe block — top-level functions are not in
  # scope unless defined inside a BeforeAll. Declare helpers here so every
  # test can use them.
  function script:Invoke-GuestPS {
    param([string] $HostName, [string] $Script)
    $output = ssh.exe -o StrictHostKeyChecking=no $HostName "powershell -NoProfile -Command `"$Script`""
    return @{
      ExitCode = $LASTEXITCODE
      Output   = ($output | Out-String).Trim()
    }
  }

  # Give SSH a moment to settle after the final boot — egs-service comes up
  # and registers Hyper-V sockets, but the Pester test process can race ahead.
  # The probe NEEDS to tolerate the early "connection refused" — keep the
  # preference flag off while it polls or a single failed ssh.exe call would
  # immediately throw.
  $savedPref = $PSNativeCommandUseErrorActionPreference
  $PSNativeCommandUseErrorActionPreference = $false
  try {
    for ($i = 0; $i -lt 40; $i++) {
      $probe = ssh.exe -o StrictHostKeyChecking=no -o ConnectTimeout=5 `
        "$($catlet.Id).eryph.alt" 'hostname' 2>$null
      if ($LASTEXITCODE -eq 0) {
        Write-Host "SSH ready after $i probe(s) — hostname=$probe"
        break
      }
      Start-Sleep -Seconds 3
    }
  }
  finally {
    $PSNativeCommandUseErrorActionPreference = $savedPref
  }
}

AfterAll {
  . $PSScriptRoot/Helpers.ps1

  # Collect agent logs + datasource contents while the catlet is still alive,
  # so a failed run leaves a diagnosable artifact set without a manual SSH dig.
  if ($catlet) {
    try {
      Save-GuestDiagnostics -CatletId $catlet.Id `
        -OutputDir (Join-Path $PSScriptRoot "diagnostics/$($catlet.Name)")
    }
    catch {
      Write-Host "Guest diagnostics collection failed (non-fatal): $_"
    }
  }

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

    It 'is running the patched binary (egs-service commit SHA matches host build)' {
      $hostName = "$($catlet.Id).eryph.alt"
      # ProductVersion carries the GitVersion InformationalVersion with the SHA
      # baked in (e.g. "0.3.1-provisioning-agent.21+Branch.X.Sha.abcdef..."). The
      # SHA is the discriminating bit — assembly version stays 0.3.0.0 across
      # builds. Pin the test to the SHA so we know the offline-patched binary
      # is what's actually running.
      $hostProductVersion = (Get-Item "$resolvedPublishPath\egs-service.exe").VersionInfo.ProductVersion
      $hostSha = if ($hostProductVersion -match 'Sha\.([0-9a-f]{7,})') { $Matches[1] } else { $null }
      $hostSha | Should -Not -BeNullOrEmpty

      $r = Invoke-GuestPS -HostName $hostName `
        -Script "& 'C:\Program Files\eryph\guest-services\bin\egs-service.exe' version"
      $r.ExitCode | Should -Be 0
      $r.Output | Should -Match ([regex]::Escape($hostSha))
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

  Context 'growpart extended the OS partition' {
    # The catlet config requests a 64 GB sda — larger than the parent gene's
    # OS image — so the freshly-booted VM has unallocated space at the end
    # of disk 0. GrowpartModule (Stage.Network, PerBoot) must extend the
    # OS partition into that space on first boot.

    It 'extended the OS partition by at least 2 GiB versus its offline size' {
      $hostName = "$($catlet.Id).eryph.alt"
      $r = Invoke-GuestPS -HostName $hostName `
        -Script '(Get-Partition -DriveLetter C).Size'
      $r.ExitCode | Should -Be 0
      $live = [uint64] $r.Output.Trim()
      $delta = $live - $script:offlineOsPartitionSize
      Write-Host ("offline=$([math]::Round($script:offlineOsPartitionSize / 1GB, 2)) GB " +
                  "live=$([math]::Round($live / 1GB, 2)) GB " +
                  "delta=$([math]::Round($delta / 1GB, 2)) GB")
      $delta | Should -BeGreaterThan ([uint64](2GB))
    }

    It 'has the OS partition close to the requested 64 GB disk size' {
      # 64 GB request minus a few hundred MB for reserved / recovery /
      # MSR partitions. 60 GB is a safe floor that catches "growpart no-op".
      $hostName = "$($catlet.Id).eryph.alt"
      $r = Invoke-GuestPS -HostName $hostName `
        -Script '(Get-Partition -DriveLetter C).Size'
      $r.ExitCode | Should -Be 0
      $live = [uint64] $r.Output.Trim()
      $live | Should -BeGreaterThan ([uint64](60GB))
    }

    It 'wrote a per-boot growpart semaphore with outcome=completed' {
      # Module key matches the FullName of GrowpartModule. The semaphore
      # is per-boot so it lives under the global sem/ dir, not the
      # per-instance one — a regression that wires the module as
      # per-instance would land it in the wrong path.
      $hostName = "$($catlet.Id).eryph.alt"
      $r = Invoke-GuestPS -HostName $hostName `
        -Script "Get-Content -Raw -LiteralPath 'C:\ProgramData\eryph\provisioning\sem\Eryph.GuestServices.Provisioning.Modules.GrowpartModule.per-boot'"
      $r.ExitCode | Should -Be 0
      $r.Output | Should -Match '"outcome":\s*"completed"'
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
