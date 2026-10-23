#Requires -Version 7.4
#Requires -RunAsAdministrator

<#
.SYNOPSIS
  End-to-end test for the ChocolateyModule (RFC 0021): at first boot the embedded
  provisioning agent bootstraps Chocolatey (as LocalSystem) and installs a
  package — "as a human user would".

.DESCRIPTION
  Same shape as Winget.E2E.Tests.ps1 / Provisioning.E2E.Tests.ps1: creates a
  BASE catlet, bakes our locally-built egs-service into its VHD offline, disables
  cloudbase-init, boots, and waits for the KVP provisioning state. The catlet's
  chocolatey: fodder is the test input. REQUIRES INTERNET in the guest (the
  bootstrap downloads install.ps1 from community.chocolatey.org).

.PREREQUISITES
  - Run as Administrator (Mount-VHD + offline reg load).
  - A running eryph (service OR dev build) reachable via egs-tool / ComputeClient.
  - egs-tool initialize completed. Guest has outbound internet.
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

  $catletConfigTemplate = Get-Content -Raw -Path $PSScriptRoot/chocolatey-catlet.yaml
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

  # Bootstrapping Chocolatey downloads install.ps1 + installs a package, so
  # allow a longer window than a plain provisioning run.
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

Describe 'ChocolateyModule at first boot' {

  It 'reports provisioning completed via KVP' {
    $kvp = egs-tool get-data --json $catlet.VmId | ConvertFrom-Json -AsHashtable
    $kvp.guest.'eryph.provisioning.state' | Should -Be 'completed'
  }

  It 'bootstrapped Chocolatey (choco.exe present)' {
    $hostName = "$($catlet.VmId).hyper-v.alt"
    $r = Invoke-GuestPS -HostName $hostName `
      -Script "Test-Path 'C:\ProgramData\chocolatey\bin\choco.exe'"
    $r.ExitCode | Should -Be 0
    $r.Output.Trim() | Should -Be 'True'
  }

  It 'installed 7-Zip via choco (the discriminating "it actually works" check)' {
    $hostName = "$($catlet.VmId).hyper-v.alt"
    $r = Invoke-GuestPS -HostName $hostName `
      -Script "Test-Path 'C:\Program Files\7-Zip\7z.exe'"
    $r.ExitCode | Should -Be 0
    $r.Output.Trim() | Should -Be 'True'
  }

  It 'choco lists 7zip.install as a local package' {
    $hostName = "$($catlet.VmId).hyper-v.alt"
    $r = Invoke-GuestPS -HostName $hostName `
      -Script "& 'C:\ProgramData\chocolatey\bin\choco.exe' list 7zip.install --limit-output"
    $r.ExitCode | Should -Be 0
    $r.Output | Should -Match '7zip\.install'
  }

  It 'logged the chocolatey package install in the agent log' {
    $localLog = Join-Path ([System.IO.Path]::GetTempPath()) "egs-choco-$($catlet.VmId).log"
    Remove-Item $localLog -ErrorAction SilentlyContinue
    egs-tool download-file $catlet.VmId 'C:\ProgramData\eryph\guest-services\logs\agent.log' $localLog
    Test-Path $localLog | Should -BeTrue
    $installed = [bool](Select-String -LiteralPath $localLog -Pattern "Installing Chocolatey package '7zip.install'" -Quiet)
    $failed = [bool](Select-String -LiteralPath $localLog -Pattern 'choco install .* failed' -Quiet)
    $installed | Should -BeTrue
    $failed | Should -BeFalse
  }
}
