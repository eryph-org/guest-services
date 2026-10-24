#Requires -Version 7.4
#Requires -RunAsAdministrator

<#
.SYNOPSIS
  End-to-end test for the WingetModule (RFC 0020): at first boot the embedded
  provisioning agent provisions winget for all users, writes the LocalSystem
  wrapper, and installs a package machine-wide — "as a human user would".

.DESCRIPTION
  Same shape as Provisioning.E2E.Tests.ps1: creates a BASE catlet, bakes our
  locally-built egs-service into its VHD offline (so the first-boot binary is
  the one under test), disables cloudbase-init, boots, and waits for the KVP
  provisioning state. The catlet's winget: fodder is the test input.

  The winsrv starter image does NOT ship App Installer, so this exercises the
  full path: ProvisionWingetAsync (GitHub download + Add-AppxProvisionedPackage)
  -> EnsureWingetWrapperAsync -> winget install 7zip.7zip. REQUIRES INTERNET in
  the guest.

.PREREQUISITES
  - Run as Administrator (Mount-VHD + offline reg load).
  - A running eryph (service OR dev build) reachable via egs-tool / ComputeClient.
  - egs-tool initialize completed. Guest has outbound internet.
#>
param (
    [Parameter()]
    [ValidateSet('winsrv2022', 'winsrv2025')]  # winget requires Server 2022+ (build 20348+)
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

  $catletConfigTemplate = Get-Content -Raw -Path $PSScriptRoot/winget-catlet.yaml
  $catletConfig = $catletConfigTemplate -replace '<<PARENT>>', "dbosoft/$OSVersion-standard/starter"

  $project = New-TestProject
  $catletName = New-CatletName
  Write-Host "Creating BASE catlet $catletName in project $($project.Name) ..."
  $catlet = New-Catlet -Config $catletConfig -Name $catletName `
    -ProjectName $project.Name `
    -Variables @{ sshPublicKey = $sshPublicKey } `
    -SkipVariablesPrompt

  $state = (Get-VM -Id $catlet.VmId).State
  if ($state -ne 'Off') { throw "Expected catlet to be Off after creation; got $state." }

  Write-Host "Replacing egs-service binaries offline (publish=$resolvedPublishPath) ..."
  Update-EgsServiceBinariesOffline -VmId $catlet.VmId -PublishPath $resolvedPublishPath

  Write-Host "Starting catlet $($catlet.Name) ..."
  Start-Catlet -Id $catlet.Id -Force

  # winget provisioning downloads App Installer + deps from GitHub and then
  # installs 7-Zip, so allow a longer window than a plain provisioning run.
  Write-Host "Waiting for provisioning to complete (KVP eryph.provisioning.state) ..."
  $finalState = Wait-ForProvisioningComplete -VmId $catlet.VmId `
    -Timeout (New-TimeSpan -Minutes 15)
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
  }
  finally {
    $PSNativeCommandUseErrorActionPreference = $savedPref
  }

  function script:Invoke-GuestPS {
    param([string] $HostName, [string] $Script)
    $output = ssh.exe -o StrictHostKeyChecking=no $HostName $Script
    return @{
      ExitCode = $LASTEXITCODE
      Output   = ($output | Out-String).Trim()
    }
  }

  # Settle SSH after the final boot (tolerate early connection refused).
  $savedPref = $PSNativeCommandUseErrorActionPreference
  $PSNativeCommandUseErrorActionPreference = $false
  try {
    for ($i = 0; $i -lt 40; $i++) {
      $probe = ssh.exe -o StrictHostKeyChecking=no -o ConnectTimeout=5 `
        "$($catlet.VmId).hyper-v.alt" 'hostname' 2>$null
      if ($LASTEXITCODE -eq 0) { Write-Host "SSH ready after $i probe(s) — hostname=$probe"; break }
      Start-Sleep -Seconds 3
    }
  }
  finally {
    $PSNativeCommandUseErrorActionPreference = $savedPref
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
    Write-Host "EGS_E2E_KEEP_VM set — leaving catlet $($catlet.Name) for inspection."
    return
  }
  if ($catlet) { Remove-Catlet -Id $catlet.Id -Force -ErrorAction SilentlyContinue }
  if ($project) { Remove-EryphProject -Id $project.Id -Force -ErrorAction SilentlyContinue }
}

Describe 'WingetModule at first boot' {

  It 'reports provisioning completed via KVP' {
    $kvp = egs-tool get-data --json $catlet.VmId | ConvertFrom-Json -AsHashtable
    $kvp.guest.'eryph.provisioning.state' | Should -Be 'completed'
  }

  It 'provisioned winget (App Installer) for all users' {
    $hostName = "$($catlet.VmId).hyper-v.alt"
    $r = Invoke-GuestPS -HostName $hostName `
      -Script "(Get-AppxProvisionedPackage -Online | Where-Object DisplayName -eq 'Microsoft.DesktopAppInstaller' | Measure-Object).Count"
    $r.ExitCode | Should -Be 0
    [int]($r.Output.Trim()) | Should -BeGreaterThan 0
  }

  It 'wrote the LocalSystem wrapper' {
    $hostName = "$($catlet.VmId).hyper-v.alt"
    $r = Invoke-GuestPS -HostName $hostName `
      -Script "Test-Path 'C:\ProgramData\eryph\winget\winget-system.ps1'"
    $r.ExitCode | Should -Be 0
    $r.Output.Trim() | Should -Be 'True'
  }

  It 'installed 7-Zip machine-wide (the discriminating "it actually works" check)' {
    $hostName = "$($catlet.VmId).hyper-v.alt"
    $r = Invoke-GuestPS -HostName $hostName `
      -Script "Test-Path 'C:\Program Files\7-Zip\7z.exe'"
    $r.ExitCode | Should -Be 0
    $r.Output.Trim() | Should -Be 'True'
  }

  It 'winget lists 7zip.7zip as installed through the wrapper' {
    $hostName = "$($catlet.VmId).hyper-v.alt"
    $r = Invoke-GuestPS -HostName $hostName `
      -Script "& 'C:\ProgramData\eryph\winget\winget-system.ps1' list --id 7zip.7zip --accept-source-agreements"
    $r.ExitCode | Should -Be 0
    $r.Output | Should -Match '7zip\.7zip'
  }

  It 'logged the winget package install in the agent log' {
    $localLog = Join-Path ([System.IO.Path]::GetTempPath()) "egs-winget-$($catlet.VmId).log"
    Remove-Item $localLog -ErrorAction SilentlyContinue
    egs-tool download-file $catlet.VmId 'C:\ProgramData\eryph\guest-services\logs\agent.log' $localLog
    Test-Path $localLog | Should -BeTrue
    $installed = [bool](Select-String -LiteralPath $localLog -Pattern "Installing winget package '7zip.7zip'" -Quiet)
    $failed = [bool](Select-String -LiteralPath $localLog -Pattern 'winget install .* failed' -Quiet)
    $installed | Should -BeTrue
    $failed | Should -BeFalse
  }
}
