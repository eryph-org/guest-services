#Requires -Version 7.4
#Requires -RunAsAdministrator

<#
.SYNOPSIS
  End-to-end test: a plain catlet (no cloud-config `hostname:`) gets its
  computer name from the datasource `local-hostname` that eryph derives from
  the catlet name.

.DESCRIPTION
  Regression coverage for "hostname is not changed". eryph sets a catlet's
  hostname via the NoCloud meta-data key `local-hostname`, not a cloud-config
  `hostname:`. SetHostnameModule used to read only the cloud-config, so a
  normal catlet kept the Windows sysprep default. This suite deploys a catlet
  with NO hostname fodder and asserts the guest computer name became the
  catlet name (NetBIOS-uppercased, truncated to 15 chars).

  Same offline-install mechanics as Provisioning.E2E.Tests.ps1: mount the VHD
  before first boot, copy our egs-service binaries in, disable cloudbase-init
  if present, boot, and let the embedded agent provision.

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

  $catletConfig = (Get-Content -Raw -Path $PSScriptRoot/hostname-catlet.yaml) `
    -replace '<<PARENT>>', "dbosoft/$OSVersion-standard/starter"

  $project = New-TestProject
  $catletName = New-CatletName
  # The Windows computer name is the catlet name, NetBIOS-normalized:
  # uppercased and truncated to 15 chars. New-CatletName yields a 15-char
  # 'egs<yyMMddHHmmss>' so no truncation is expected, but normalize anyway.
  $script:expectedComputerName = $catletName.ToUpperInvariant()
  if ($script:expectedComputerName.Length -gt 15) {
    $script:expectedComputerName = $script:expectedComputerName.Substring(0, 15)
  }

  Write-Host "Creating PLAIN catlet $catletName (no hostname fodder); expected computer name $script:expectedComputerName ..."
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
    } catch { Write-Host "Guest diagnostics collection failed (non-fatal): $_" }
  }
  if ($env:EGS_E2E_KEEP_VM) {
    Write-Host "EGS_E2E_KEEP_VM set — leaving catlet $($catlet.Name) for inspection."
    return
  }
  if ($catlet) { Remove-Catlet -Id $catlet.Id -Force -ErrorAction SilentlyContinue }
  if ($project) { Remove-EryphProject -Id $project.Id -Force -ErrorAction SilentlyContinue }
}

Describe 'Hostname from datasource local-hostname' {

  It 'set the computer name to the catlet name (no cloud-config hostname)' {
    $hostName = "$($catlet.VmId).hyper-v.alt"
    $r = Invoke-GuestPS -HostName $hostName -Script '$env:COMPUTERNAME'
    $r.ExitCode | Should -Be 0
    $r.Output | Should -Be $script:expectedComputerName
  }

  It 'is not the Windows sysprep default (WIN-*)' {
    $hostName = "$($catlet.VmId).hyper-v.alt"
    $r = Invoke-GuestPS -HostName $hostName -Script '$env:COMPUTERNAME'
    $r.ExitCode | Should -Be 0
    $r.Output | Should -Not -Match '^WIN-'
  }
}
