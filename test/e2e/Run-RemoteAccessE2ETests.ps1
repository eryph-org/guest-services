#Requires -Version 7.4
<#
.SYNOPSIS
  Runs the remote-access client-auth e2e tests against a fresh eryph catlet.
  Covers multi-key authorization, add-ssh-config dual-write, and the
  KvpAuthEnabled hardening gate.

.DESCRIPTION
  Uses the offline-injection pattern (mirrors Run-ProvisioningE2ETests.ps1):
  publishes the local egs-service and egs-tool, creates a stopped catlet,
  mounts its VHD, replaces egs-service binaries, writes id_egs.pub (host's
  egs-tool public key) into the offline image, disables cloudbase-init,
  then starts the catlet. First boot runs the new build end-to-end.
  Catlet + project are torn down in AfterAll unless $env:EGS_E2E_KEEP_VM
  is set.

  Requires:
    - Elevated PowerShell 7.4+ (Hyper-V WMI + egs-tool admin paths)
    - eryph host with `egs-tool initialize` already run
    - Pester 5.x, Eryph.ComputeClient
    - The dbosoft/$OSVersion-standard/starter parent gene cached locally
#>
param (
    [Parameter()]
    [ValidateSet('winsrv2019', 'winsrv2022', 'winsrv2025')]
    [string] $OSVersion = 'winsrv2022',

    [Parameter()]
    [switch] $SkipBuild
)

$PSNativeCommandUseErrorActionPreference = $true
$ErrorActionPreference = 'Stop'

Write-Host "Installing required PowerShell modules ..."
Install-Module -Name Pester -MinimumVersion 5.5 -Force -Scope CurrentUser -SkipPublisherCheck
Install-Module -Name Eryph.ComputeClient -Force -Scope CurrentUser

if (-not $SkipBuild) {
  Write-Host "Publishing egs-service for win-x64 ..."
  dotnet publish "$PSScriptRoot/../../src/Eryph.GuestServices.Service/Eryph.GuestServices.Service.csproj" `
    -c Release -r win-x64 --nologo

  Write-Host "Publishing egs-tool for win-x64 ..."
  dotnet publish "$PSScriptRoot/../../src/Eryph.GuestServices.Tool/Eryph.GuestServices.Tool.csproj" `
    -c Release -r win-x64 --nologo
}

$publishPath = Resolve-Path "$PSScriptRoot/../../src/Eryph.GuestServices.Service/bin/Release/net10.0/win-x64/publish"
$toolPublishPath = Resolve-Path "$PSScriptRoot/../../src/Eryph.GuestServices.Tool/bin/Release/net10.0-windows/win-x64/publish"

# Use the locally-built egs-tool for this session — the named-slot
# dual-write in add-ssh-config is only in the local build.
$env:Path = "$($toolPublishPath.Path);$env:Path"
Write-Host "Using egs-tool from: $toolPublishPath"
Write-Host "egs-tool version: $(egs-tool --version)"

Write-Host "Running Pester suite ..."
$container = New-PesterContainer -Path "$PSScriptRoot/RemoteAccess.E2E.Tests.ps1" `
  -Data @{
    OSVersion = $OSVersion
    PublishPath = $publishPath.Path
  }

$config = New-PesterConfiguration
$config.Output.Verbosity = 'Detailed'
$config.Run.Container = $container
$config.Run.Exit = $true
$config.TestResult.Enabled = $true
$config.TestResult.OutputFormat = 'NUnit3'
$config.TestResult.OutputPath = "$PSScriptRoot/TEST-remoteaccess-pesterResults.xml"

Invoke-Pester -Configuration $config
