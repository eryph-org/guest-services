#Requires -Version 7.4
#Requires -RunAsAdministrator

<#
.SYNOPSIS
  End-to-end test: the SshModule leaves the built-in Win32-OpenSSH server
  Running AND set to start Automatically — including when it merely FINDS an
  already-installed daemon that is not set to autostart.

.DESCRIPTION
  The catlet config requests `ssh.install_openssh: true`, so the module installs
  the OpenSSH server capability on first boot. The suite asserts the resulting
  sshd service is Running and StartType=Automatic (so ssh survives a reboot),
  then simulates the reported bug — a daemon found at Manual/Stopped — by
  flipping sshd back to Manual+Stopped and re-running the Config stage
  (`egs-service reset` + `egs-service run --stage config`), and asserts the
  module re-enables it to Automatic+Running.

  Same offline-install mechanics as the other suites: mount the VHD before first
  boot, copy our egs-service binaries in, disable cloudbase-init if present,
  boot, and let the embedded agent provision.

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

  $catletConfig = (Get-Content -Raw -Path $PSScriptRoot/ssh-server-catlet.yaml) `
    -replace '<<PARENT>>', "dbosoft/$OSVersion-standard/starter"

  $project = New-TestProject
  $catletName = New-CatletName
  Write-Host "Creating OpenSSH catlet $catletName (ssh.install_openssh: true) ..."
  $catlet = New-Catlet -Config $catletConfig -Name $catletName -ProjectName $project.Name

  $state = (Get-VM -Id $catlet.VmId).State
  if ($state -ne 'Off') { throw "Expected catlet to be Off after creation; got $state." }

  Write-Host "Replacing egs-service binaries offline (publish=$resolvedPublishPath) ..."
  Update-EgsServiceBinariesOffline -VmId $catlet.VmId -PublishPath $resolvedPublishPath

  Write-Host "Starting catlet $($catlet.Name) ..."
  Start-Catlet -Id $catlet.Id -Force

  Write-Host "Waiting for provisioning to complete (KVP eryph.provisioning.state) ..."
  $finalState = Wait-ForProvisioningComplete -VmId $catlet.VmId -Timeout (New-TimeSpan -Minutes 12)
  Write-Host "Provisioning state: $finalState"

  Write-Host "Waiting for egs-service SSH listener (get-status=available) ..."
  $savedPref = $PSNativeCommandUseErrorActionPreference
  $PSNativeCommandUseErrorActionPreference = $false
  try {
    Wait-Assert -Timeout (New-TimeSpan -Minutes 5) -Interval (New-TimeSpan -Seconds 3) {
      $status = egs-tool get-status $catlet.VmId
      if ($status -ne 'available') { throw "guest services not available: $status" }
    }
    egs-tool add-ssh-config $catlet.VmId | Out-Null
    for ($i = 0; $i -lt 40; $i++) {
      ssh.exe -o StrictHostKeyChecking=no -o ConnectTimeout=5 "$($catlet.VmId).hyper-v.alt" 'hostname' 2>$null | Out-Null
      if ($LASTEXITCODE -eq 0) { break }
      Start-Sleep -Seconds 3
    }
  }
  finally { $PSNativeCommandUseErrorActionPreference = $savedPref }

  function script:Invoke-GuestPS {
    param([string] $HostName, [string] $Script)
    # egs runs every SSH exec command through `powershell -Command`, so send the
    # bare script (re-wrapping would double-evaluate it — issue #52).
    $output = ssh.exe -o StrictHostKeyChecking=no $HostName $Script
    return @{ ExitCode = $LASTEXITCODE; Output = ($output | Out-String).Trim() }
  }

  # Reads sshd's service state as "<StartType>/<Status>", e.g. "Automatic/Running".
  # Get-Service.StartType is the persisted start mode (what survives a reboot).
  function script:Get-SshdState {
    param([string] $HostName)
    $r = Invoke-GuestPS -HostName $HostName `
      -Script '$s = Get-Service sshd -ErrorAction SilentlyContinue; if ($s) { "$($s.StartType)/$($s.Status)" } else { "absent" }'
    return $r.Output.Trim()
  }
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

Describe 'Built-in ssh server is enabled and running' {

  Context 'after provisioning (install path)' {

    It 'installed the sshd service' {
      $hostName = "$($catlet.VmId).hyper-v.alt"
      Get-SshdState -HostName $hostName | Should -Not -Be 'absent'
    }

    It 'set sshd to start Automatically (survives a reboot)' {
      $hostName = "$($catlet.VmId).hyper-v.alt"
      (Get-SshdState -HostName $hostName) -split '/' | Select-Object -First 1 | Should -Be 'Automatic'
    }

    It 'left sshd Running' {
      $hostName = "$($catlet.VmId).hyper-v.alt"
      (Get-SshdState -HostName $hostName) -split '/' | Select-Object -Last 1 | Should -Be 'Running'
    }
  }

  Context 'a found daemon in a non-Automatic state is re-enabled (regression)' {

    It 're-enables sshd to Automatic + Running when it is found at Manual/Stopped' {
      $hostName = "$($catlet.VmId).hyper-v.alt"

      # 1. Simulate an image whose sshd is present but NOT set to autostart.
      $flip = Invoke-GuestPS -HostName $hostName `
        -Script 'Set-Service sshd -StartupType Manual; Stop-Service sshd -Force; $s = Get-Service sshd; "$($s.StartType)/$($s.Status)"'
      $flip.ExitCode | Should -Be 0
      $flip.Output.Trim() | Should -Be 'Manual/Stopped' -Because 'the pre-condition is a found, non-autostart daemon'

      # 2. Re-run the Config stage so the SshModule sees the already-installed
      #    (found) daemon. reset clears the per-instance semaphore so the module
      #    actually re-runs; --stage config avoids the Network-stage reboot.
      $egs = "C:\Program Files\eryph\guest-services\bin\egs-service.exe"
      $reset = Invoke-GuestPS -HostName $hostName -Script "& '$egs' reset"
      $reset.ExitCode | Should -Be 0
      $run = Invoke-GuestPS -HostName $hostName -Script "& '$egs' run --stage config"
      $run.ExitCode | Should -Be 0

      # 3. The module must have set the found daemon back to Automatic + Running.
      Get-SshdState -HostName $hostName | Should -Be 'Automatic/Running' `
        -Because 'a found sshd must be enabled for autostart, not just configured'
    }

    It 'does not hang and stays Automatic + Running when sshd is found already RUNNING' {
      # The dangerous precondition: sshd is found ALREADY RUNNING. A running
      # daemon holds the ssh_host_*_key files open, which made host-key
      # regeneration hang on ssh-keygen's overwrite prompt. The module must stop
      # sshd before regenerating (then restart + enable autostart).
      $hostName = "$($catlet.VmId).hyper-v.alt"

      $flip = Invoke-GuestPS -HostName $hostName `
        -Script 'Set-Service sshd -StartupType Automatic; Start-Service sshd; $s = Get-Service sshd; "$($s.StartType)/$($s.Status)"'
      $flip.ExitCode | Should -Be 0
      $flip.Output.Trim() | Should -Be 'Automatic/Running'

      $egs = "C:\Program Files\eryph\guest-services\bin\egs-service.exe"
      (Invoke-GuestPS -HostName $hostName -Script "& '$egs' reset").ExitCode | Should -Be 0

      # Bound the re-run so a host-key-lock regression fails this test cleanly
      # instead of hanging the suite. A healthy Config stage finishes in seconds.
      $job = Start-Job -ArgumentList $hostName, $egs -ScriptBlock {
        param($h, $e)
        ssh.exe -o StrictHostKeyChecking=no $h "& '$e' run --stage config" | Out-Null
        $LASTEXITCODE
      }
      $finished = Wait-Job $job -Timeout 120
      $finished | Should -Not -BeNullOrEmpty `
        -Because 'run --stage config must not hang on a running-sshd host-key lock'
      $exit = [int](Receive-Job $job | Select-Object -Last 1)
      Remove-Job $job -Force
      $exit | Should -Be 0

      Get-SshdState -HostName $hostName | Should -Be 'Automatic/Running'
    }
  }
}
