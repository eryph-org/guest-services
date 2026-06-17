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
  - We do NOT add the dbosoft/guest-services gene; the parent already ships
    egs-service baked into C:\Program Files\eryph\guest-services\bin\. We
    overwrite those binaries with our locally-built ones offline via VHD mount.
  - Newer base images no longer ship cloudbase-init (egs-service is the sole
    provisioning engine). The offline cloudbase-init disable is therefore
    best-effort and a no-op when cbi is absent (Test-Path guarded); it stays
    in place for older images that still bundle it.
  - Before first start we mount the catlet's VHD, copy our locally-built
    egs-service binaries into the existing bin dir, and (if present) disable
    cloudbase-init by renaming its install dir AND setting Start=Disabled.
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
    Write-Host "egs-service is available — writing SSH config for the VM ..."
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
    # egs runs every SSH exec command through its configured shell, which on
    # Windows is `powershell -Command "<command>"` (since #42). Send the bare
    # script: re-wrapping it in another `powershell -Command` here would make
    # the guest evaluate it TWICE, so the outer shell expands $env:COMPUTERNAME
    # / $z / $e before the inner powershell ever runs (issue #52).
    $output = ssh.exe -o StrictHostKeyChecking=no $HostName $Script
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
        "$($catlet.VmId).hyper-v.alt" 'hostname' 2>$null
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
      # The bespoke eryph.provisioning.* keys (instance/stage/error/...) were
      # dropped in RFC 0031 — only the single state key remains on Surface 2.
      $kvp.guest.ContainsKey('eryph.provisioning.error') | Should -BeFalse
      $kvp.guest.ContainsKey('eryph.provisioning.instance') | Should -BeFalse
    }

    It 'emits the cloud-init CLOUD_INIT event stream over KVP (Surface 1)' {
      # RFC 0031 Surface 1: egs stands in for cloud-init on Windows and emits
      # the same CLOUD_INIT|<incarnation>|<type>|<name>|<uuid> stream a Linux
      # catlet would get from cloud-init natively. A completed run must carry a
      # `finish modules-final SUCCESS` event.
      $kvp = egs-tool get-data --json $catlet.VmId | ConvertFrom-Json -AsHashtable
      $cloudInitKeys = $kvp.guest.Keys | Where-Object { $_ -like 'CLOUD_INIT|*' }
      $cloudInitKeys | Should -Not -BeNullOrEmpty

      $finalFinish = $cloudInitKeys | Where-Object { $_ -like 'CLOUD_INIT|*|finish|modules-final|*' }
      $finalFinish | Should -Not -BeNullOrEmpty
      # get-data --json already nests the event JSON, so -AsHashtable yields the
      # parsed event object — index it directly, do not parse a second time.
      $value = $kvp.guest[($finalFinish | Select-Object -First 1)]
      $value.name | Should -Be 'modules-final'
      $value.result | Should -Be 'SUCCESS'
    }

    It 'wrote the state file inside the guest' {
      $hostName = "$($catlet.VmId).hyper-v.alt"
      $r = Invoke-GuestPS -HostName $hostName `
        -Script 'Get-Content -Raw -LiteralPath C:\ProgramData\eryph\provisioning\state.json'
      $r.ExitCode | Should -Be 0
      $state = $r.Output | ConvertFrom-Json -AsHashtable
      $state.instanceId | Should -Not -BeNullOrEmpty
      $state.completedStages | Should -Contain 'Final'
    }

    It 'is running the patched binary (egs-service commit SHA matches host build)' {
      $hostName = "$($catlet.VmId).hyper-v.alt"
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
      # `egs-service version` (Spectre.Console) hard-wraps at 80 columns when
      # stdout is redirected (no TTY), so the SHA can straddle a newline. Strip
      # all whitespace before matching so a wrapped SHA still compares (#52).
      ($r.Output -replace '\s', '') | Should -Match ([regex]::Escape($hostSha))
    }

    It 'did not run cloudbase-init' {
      $hostName = "$($catlet.VmId).hyper-v.alt"
      $r = Invoke-GuestPS -HostName $hostName `
        -Script '(Get-Service cloudbase-init -ErrorAction SilentlyContinue).Status'
      if ($r.Output) { $r.Output | Should -Not -Be 'Running' }
    }
  }

  Context 'cloud-config payload was applied' {

    It 'SetHostnameModule set the computer name to egs-prov-e2e' {
      $hostName = "$($catlet.VmId).hyper-v.alt"
      $r = Invoke-GuestPS -HostName $hostName -Script '$env:COMPUTERNAME'
      $r.ExitCode | Should -Be 0
      $r.Output | Should -Be 'egs-prov-e2e'
    }

    It 'UsersGroupsModule created prov_test_user in Administrators' {
      $hostName = "$($catlet.VmId).hyper-v.alt"
      $r = Invoke-GuestPS -HostName $hostName `
        -Script '(Get-LocalGroupMember -Group Administrators -Member prov_test_user -ErrorAction SilentlyContinue).Name'
      $r.ExitCode | Should -Be 0
      $r.Output | Should -Match 'prov_test_user'
    }

    # --- NON-EXPIRED password (prov_test_user, users: block) -------------------

    It 'created prov_test_user with a WORKING password (can authenticate)' {
      # The whole point of user creation: the provisioned password must actually
      # log the user in. ValidateCredentials returns true only when the password
      # matches AND the account is enabled, not locked, and NOT expired — so this
      # also guards the level-1017/acct_expires regression end-to-end.
      $hostName = "$($catlet.VmId).hyper-v.alt"
      $r = Invoke-GuestPS -HostName $hostName `
        -Script "Add-Type -AssemblyName System.DirectoryServices.AccountManagement; (New-Object System.DirectoryServices.AccountManagement.PrincipalContext('Machine')).ValidateCredentials('prov_test_user','XyzqW3lc0me!2!')"
      $r.ExitCode | Should -Be 0
      $r.Output.Trim() | Should -Be 'True'
    }

    It 'did NOT force a password change for prov_test_user (users: block)' {
      # The users: block never forces a change, so usri4_password_expired must be
      # 0. WinNT's PasswordExpired exposes that flag; read .Value, because the raw
      # property accessor does not reliably surface the integer.
      $hostName = "$($catlet.VmId).hyper-v.alt"
      $r = Invoke-GuestPS -HostName $hostName `
        -Script "([ADSI]'WinNT://./prov_test_user,user').PasswordExpired.Value"
      $r.ExitCode | Should -Be 0
      $r.Output.Trim() | Should -Be '0'
    }

    It 'left prov_test_user enabled' {
      $hostName = "$($catlet.VmId).hyper-v.alt"
      $r = Invoke-GuestPS -HostName $hostName `
        -Script '(Get-LocalUser prov_test_user).Enabled'
      $r.ExitCode | Should -Be 0
      $r.Output.Trim() | Should -Be 'True'
    }

    It 'did NOT expire prov_test_user''s account' {
      # Get-LocalUser reports a never-expiring account as $null/empty.
      $hostName = "$($catlet.VmId).hyper-v.alt"
      $r = Invoke-GuestPS -HostName $hostName `
        -Script '(Get-LocalUser prov_test_user).AccountExpires'
      $r.ExitCode | Should -Be 0
      $r.Output | Should -BeNullOrEmpty
    }

    # --- EXPIRED password (prov_expire_user, chpasswd.expire: true) ------------

    It 'forced prov_expire_user to change password at next logon (chpasswd.expire: true)' {
      # chpasswd.expire:true sets usri4_password_expired = 1 via level 4. This is
      # the correct field; the old code wrote level 1017 (acct_expires) here and
      # expired the whole account instead.
      $hostName = "$($catlet.VmId).hyper-v.alt"
      $r = Invoke-GuestPS -HostName $hostName `
        -Script "([ADSI]'WinNT://./prov_expire_user,user').PasswordExpired.Value"
      $r.ExitCode | Should -Be 0
      $r.Output.Trim() | Should -Be '1'
    }

    It 'did NOT expire prov_expire_user''s ACCOUNT (must-change is not account expiry)' {
      # The crux of the bug: forcing a password change must NOT expire the
      # account. Even with must-change set, AccountExpires must stay "never".
      $hostName = "$($catlet.VmId).hyper-v.alt"
      $r = Invoke-GuestPS -HostName $hostName `
        -Script '(Get-LocalUser prov_expire_user).AccountExpires'
      $r.ExitCode | Should -Be 0
      $r.Output | Should -BeNullOrEmpty
    }

    It 'left prov_expire_user enabled' {
      $hostName = "$($catlet.VmId).hyper-v.alt"
      $r = Invoke-GuestPS -HostName $hostName `
        -Script '(Get-LocalUser prov_expire_user).Enabled'
      $r.ExitCode | Should -Be 0
      $r.Output.Trim() | Should -Be 'True'
    }

    It 'WriteFilesModule produced the plain marker file' {
      $hostName = "$($catlet.VmId).hyper-v.alt"
      $r = Invoke-GuestPS -HostName $hostName `
        -Script 'Get-Content -Raw -LiteralPath C:\ProgramData\eryph-e2e\marker-plain.txt'
      $r.ExitCode | Should -Be 0
      $r.Output.Trim() | Should -Be 'marker-plain'
    }

    It 'WriteFilesModule decoded the base64 marker file' {
      $hostName = "$($catlet.VmId).hyper-v.alt"
      $r = Invoke-GuestPS -HostName $hostName `
        -Script 'Get-Content -Raw -LiteralPath C:\ProgramData\eryph-e2e\marker-b64.txt'
      $r.ExitCode | Should -Be 0
      $r.Output.Trim() | Should -Be 'marker-b64'
    }

    It 'RuncmdModule executed the runcmd entry' {
      $hostName = "$($catlet.VmId).hyper-v.alt"
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
      $hostName = "$($catlet.VmId).hyper-v.alt"
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
      $hostName = "$($catlet.VmId).hyper-v.alt"
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
      $hostName = "$($catlet.VmId).hyper-v.alt"
      $r = Invoke-GuestPS -HostName $hostName `
        -Script "Get-Content -Raw -LiteralPath 'C:\ProgramData\eryph\provisioning\sem\Eryph.GuestServices.Provisioning.Modules.GrowpartModule.per-boot'"
      $r.ExitCode | Should -Be 0
      $r.Output | Should -Match '"outcome":\s*"completed"'
    }
  }

  Context 'CLI on the guest' {

    It 'egs-service status --json reports Final completed' {
      $hostName = "$($catlet.VmId).hyper-v.alt"
      $r = Invoke-GuestPS -HostName $hostName `
        -Script "& 'C:\Program Files\eryph\guest-services\bin\egs-service.exe' status --json"
      $r.ExitCode | Should -Be 0
      ($r.Output | ConvertFrom-Json -AsHashtable).completedStages | Should -Contain 'Final'
    }

    It 'collect-logs captures the agent''s own operational log (issue #45)' {
      # The agent writes its operational log to a file
      # (%ProgramData%\eryph\guest-services\logs\agent.log) so the support
      # bundle is useful even when no user-data scripts ran — without it the
      # only sink was the Windows Event Log, which collect-logs never reads.
      # Build the bundle, then inspect it in-guest: the egs SSH server has no
      # SFTP subsystem so scp can't pull it. Invoke-GuestPS sends the script for
      # a single guest-side powershell evaluation (issue #52), so the probe's
      # $z / $e locals are evaluated by the guest, not pre-expanded host-side.
      $hostName = "$($catlet.VmId).hyper-v.alt"
      $build = Invoke-GuestPS -HostName $hostName `
        -Script "& 'C:\Program Files\eryph\guest-services\bin\egs-service.exe' collect-logs C:\Windows\Temp\egs-e2e-bundle.zip"
      $build.ExitCode | Should -Be 0

      $probe = @'
Add-Type -AssemblyName System.IO.Compression.FileSystem; $z=[System.IO.Compression.ZipFile]::OpenRead('C:\Windows\Temp\egs-e2e-bundle.zip'); try { $e=$z.Entries | Where-Object FullName -eq 'logs/agent.log'; if ($e) { 'agent.log=' + $e.Length } else { 'agent.log=MISSING' } } finally { $z.Dispose() }
'@
      $r = Invoke-GuestPS -HostName $hostName -Script $probe
      $r.ExitCode | Should -Be 0
      $r.Output.Trim() | Should -Match '^agent\.log=\d+$'
      [int]($r.Output.Trim() -replace 'agent\.log=', '') | Should -BeGreaterThan 0
    }
  }

  Context 'egs settings' {

    It 'EgsModule wrote the service-control flags to the registry' {
      # The catlet fodder sets egs.settings.remote_access:true + kvp_auth:true.
      # The module writes them as REG_DWORDs under HKLM\SOFTWARE\eryph\guest-services
      # (read at the next service start). Use reg.exe (no $-variables) so the
      # probe survives the harness's powershell wrapping; assert both = 0x1.
      $hostName = "$($catlet.VmId).hyper-v.alt"
      $r = Invoke-GuestPS -HostName $hostName `
        -Script 'reg query "HKLM\SOFTWARE\eryph\guest-services"'
      $r.ExitCode | Should -Be 0
      $r.Output | Should -Match 'RemoteAccessEnabled\s+REG_DWORD\s+0x1'
      $r.Output | Should -Match 'KvpAuthEnabled\s+REG_DWORD\s+0x1'
    }
  }

  Context 'auto-update loop' {

    It 'started the background auto-patch loop (enabled by default)' {
      # Auto-patch is opt-out, so even this provisioned guest starts the loop.
      # It only logs "enabled" + schedules a check 36-48h out (never fires in the
      # test), proving the service is wired and gated on by default. Asserted
      # from the agent log pulled host-side, matched as booleans (no log echoed).
      $localLog = Join-Path ([System.IO.Path]::GetTempPath()) "egs-au-$($catlet.VmId).log"
      Remove-Item $localLog -ErrorAction SilentlyContinue
      egs-tool download-file $catlet.VmId 'C:\ProgramData\eryph\guest-services\logs\agent.log' $localLog
      Test-Path $localLog | Should -BeTrue

      $enabled = [bool](Select-String -LiteralPath $localLog -Pattern 'Auto-update enabled' -Quiet)
      $disabled = [bool](Select-String -LiteralPath $localLog -Pattern 'Auto-update disabled' -Quiet)
      $enabled | Should -BeTrue
      $disabled | Should -BeFalse
    }
  }

  Context 'network-config' {

    It 'matched the NIC by MAC and applied the network-config (not skipped)' {
      # Regression for issue #53: the module enumerates adapters via .NET
      # NetworkInterface.GetPhysicalAddress, so it finds the eth0 NIC by MAC and
      # applies the eryph (DHCP) network-config instead of logging "no matching
      # adapter; skipping" — which is what the empty MSFT_NetAdapter.MacAddress
      # used to cause. Asserted from the agent log, pulled host-side via
      # download-file and matched as booleans so no log content is echoed on
      # failure.
      $localLog = Join-Path ([System.IO.Path]::GetTempPath()) "egs-net-$($catlet.VmId).log"
      Remove-Item $localLog -ErrorAction SilentlyContinue
      egs-tool download-file $catlet.VmId 'C:\ProgramData\eryph\guest-services\logs\agent.log' $localLog
      Test-Path $localLog | Should -BeTrue

      # The DHCP apply path logs "DHCP requested — leaving IPv4 alone" only after
      # the adapter is matched.
      $matched = [bool](Select-String -LiteralPath $localLog -Pattern 'DHCP requested' -Quiet)
      $skipped = [bool](Select-String -LiteralPath $localLog -Pattern 'no matching adapter' -Quiet)
      $matched | Should -BeTrue
      $skipped | Should -BeFalse
    }
  }

  Context 'self-update download' {

    It 'resolves, downloads and signature-verifies the released package (no apply)' {
      # Exercises the real auto-update path UP TO staging — resolve target,
      # download the package + signed SHA256SUMS from releases.dbosoft.eu, verify
      # the OpenPGP signature and the package hash, extract — without applying it
      # (no service stop / swap). The guest runs a dev build, so pinning 0.4.0
      # is a real cross-version download. A printed "STAGED 0.4.0" proves the
      # signature + hash verified (PrepareAsync only stages on success).
      $hostName = "$($catlet.VmId).hyper-v.alt"
      $r = Invoke-GuestPS -HostName $hostName `
        -Script "& 'C:\Program Files\eryph\guest-services\bin\egs-service.exe' check-update --version 0.4.0"
      $r.ExitCode | Should -Be 0
      ($r.Output -replace '\s', '') | Should -Match 'STAGED0\.4\.0'
    }
  }

  Context 'self-update process' {

    # Runs LAST: it restarts the service. Exercises the mechanical half of the
    # self-update (the `apply-update` verb: stop service -> swap install dir ->
    # restart, with rollback on failure) end-to-end in a real guest, with the
    # SAME version (a reinstall) — no network, signing, or version bump needed.
    # The download/verify/resolve half is covered by unit tests.
    It 'apply-update swaps the install dir and the service returns healthy' {
      $hostName = "$($catlet.VmId).hyper-v.alt"

      # 1. Stage a copy of the live install dir. (Single-quoted, no $-vars and no
      #    embedded double quotes, so it survives the harness powershell wrap.)
      $stage = Invoke-GuestPS -HostName $hostName -Script @'
Remove-Item -Recurse -Force 'C:\Windows\Temp\egs-stage' -ErrorAction SilentlyContinue
Copy-Item -Recurse 'C:\Program Files\eryph\guest-services\bin' 'C:\Windows\Temp\egs-stage'
Test-Path 'C:\Windows\Temp\egs-stage\egs-service.exe'
'@
      $stage.ExitCode | Should -Be 0
      $stage.Output.Trim() | Should -Be 'True'

      # 2. Write the updater invocation to a .cmd and register a one-shot SYSTEM
      #    scheduled task. The task detaches apply-update from the SSH session
      #    and the service, so stopping the service can't kill the updater. The
      #    8.3 short path (C:\PROGRA~1) avoids quoting "Program Files" — embedded
      #    double quotes would not survive the harness wrapping on this branch.
      $write = Invoke-GuestPS -HostName $hostName -Script @'
Set-Content -LiteralPath 'C:\Windows\Temp\egs-apply.cmd' -Value 'C:\Windows\Temp\egs-stage\egs-service.exe apply-update --from C:\Windows\Temp\egs-stage --to C:\PROGRA~1\eryph\guest-services\bin --service eryph-guest-services'
schtasks /create /tn egs-e2e-apply /tr C:\Windows\Temp\egs-apply.cmd /sc once /st 23:59 /ru SYSTEM /rl HIGHEST /f | Out-Null
Test-Path 'C:\Windows\Temp\egs-apply.cmd'
'@
      $write.ExitCode | Should -Be 0
      $write.Output.Trim() | Should -Be 'True'

      # 3. Run ONLY the upgrade trigger as a background job. Triggering the task
      #    restarts the service, which severs THIS very SSH channel mid-call — a
      #    synchronous ssh.exe (no timeout) would then block the host until the
      #    outer kill. Firing it from a job and reaping with a timeout lets the
      #    channel close without wedging the suite.
      $vmId = $catlet.VmId
      $upgrade = Start-Job -ArgumentList $hostName -ScriptBlock {
        param($h)
        ssh.exe -o StrictHostKeyChecking=no $h "powershell -NoProfile -Command schtasks /run /tn egs-e2e-apply" 2>&1
      }
      Wait-Job $upgrade -Timeout 30 | Out-Null
      Remove-Job $upgrade -Force

      # 4. Poll for the service to come back. get-status rides the SAME Hyper-V
      #    channel and has no connect timeout, so a poll that lands exactly as
      #    the service drops blocks forever on the severed connection. Run each
      #    poll as a short-lived job so a stuck call is abandoned and retried on
      #    a fresh connection (same reason the trigger above is a job).
      $back = $false
      $deadline = (Get-Date).AddMinutes(4)
      while ((Get-Date) -lt $deadline) {
        $poll = Start-Job -ArgumentList $vmId -ScriptBlock {
          param($id)
          $PSNativeCommandUseErrorActionPreference = $false
          (egs-tool get-status $id) 2>$null
        }
        if (Wait-Job $poll -Timeout 15) {
          $status = (Receive-Job $poll) -join ''
          Remove-Job $poll -Force
          if ($status -match 'available') { $back = $true; break }
        }
        else {
          Stop-Job $poll; Remove-Job $poll -Force
        }
        Start-Sleep -Seconds 5
      }
      $back | Should -BeTrue -Because 'the service must restart after the binary swap'

      # 5. Provisioning state survived the swap (state.json untouched). The
      #    channel can still blip in the seconds after the service first reports
      #    available, so this read is bounded + retried too (every egs-channel
      #    call in the restart window must be, or a blip wedges the suite).
      $kvpJson = $null
      for ($i = 0; $i -lt 6 -and -not $kvpJson; $i++) {
        $j = Start-Job -ArgumentList $vmId -ScriptBlock {
          param($id)
          $PSNativeCommandUseErrorActionPreference = $false
          (egs-tool get-data --json $id) 2>$null
        }
        if (Wait-Job $j -Timeout 20) { $kvpJson = (Receive-Job $j) -join "`n" }
        else { Stop-Job $j }
        Remove-Job $j -Force
        if (-not $kvpJson) { Start-Sleep -Seconds 5 }
      }
      $kvpJson | Should -Not -BeNullOrEmpty
      $kvp = $kvpJson | ConvertFrom-Json -AsHashtable
      $kvp.guest.'eryph.provisioning.state' | Should -Be 'completed'

      # 6. The running binary is the swapped-in (patched) build — proving the
      #    swap applied the staged files and did NOT roll back. Bounded ssh
      #    (ConnectTimeout + job); SHA compared whitespace-insensitively
      #    (Spectre wraps at 80 cols without a TTY).
      $hostProductVersion = (Get-Item "$resolvedPublishPath\egs-service.exe").VersionInfo.ProductVersion
      $hostSha = if ($hostProductVersion -match 'Sha\.([0-9a-f]{7,})') { $Matches[1] } else { $null }
      $hostSha | Should -Not -BeNullOrEmpty
      $verOut = $null
      for ($i = 0; $i -lt 6 -and -not $verOut; $i++) {
        $j = Start-Job -ArgumentList $hostName -ScriptBlock {
          param($h)
          ssh.exe -o StrictHostKeyChecking=no -o ConnectTimeout=10 $h `
            "& 'C:\Program Files\eryph\guest-services\bin\egs-service.exe' version" 2>&1
        }
        if (Wait-Job $j -Timeout 25) { $verOut = (Receive-Job $j) -join '' }
        else { Stop-Job $j }
        Remove-Job $j -Force
        if (-not $verOut) { Start-Sleep -Seconds 5 }
      }
      $verOut | Should -Not -BeNullOrEmpty
      ($verOut -replace '\s', '') | Should -Match ([regex]::Escape($hostSha))
    }
  }
}
