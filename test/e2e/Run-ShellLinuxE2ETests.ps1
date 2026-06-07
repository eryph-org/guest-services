#Requires -Version 7.4
<#
.SYNOPSIS
  Runs the exec / configurable-shell e2e tests against a fresh eryph Ubuntu
  catlet.

.DESCRIPTION
  Publishes egs-service linux-x64 and egs-tool win-x64 (the tool always runs
  on the host), spins up an Ubuntu catlet, swaps the gene-installed
  egs-service with the local build over direct sshd, restarts the service,
  then runs the Pester suite. Catlet + project are torn down in AfterAll
  unless $env:EGS_E2E_KEEP_VM is set.

  Requires:
    - Elevated PowerShell 7.4+ (egs-tool admin paths)
    - eryph host with `egs-tool initialize` already run
    - Pester 5.x, Eryph.ComputeClient
    - The dbosoft/$OSVersion/starter parent gene + the
      dbosoft/starter-food/2.0:linux-starter fodder gene cached locally
#>
param (
    [Parameter()]
    [string] $OSVersion = 'ubuntu-24.04',

    [Parameter()]
    [switch] $SkipBuild
)

$PSNativeCommandUseErrorActionPreference = $true
$ErrorActionPreference = 'Stop'

Write-Host "Installing required PowerShell modules ..."
Install-Module -Name Pester -MinimumVersion 5.5 -Force -Scope CurrentUser -SkipPublisherCheck
Install-Module -Name Eryph.ComputeClient -Force -Scope CurrentUser

if (-not $SkipBuild) {
  Write-Host "Publishing egs-service for linux-x64 ..."
  dotnet publish "$PSScriptRoot/../../src/Eryph.GuestServices.Service/Eryph.GuestServices.Service.csproj" `
    -c Release -r linux-x64 --nologo

  Write-Host "Publishing egs-tool for win-x64 ..."
  dotnet publish "$PSScriptRoot/../../src/Eryph.GuestServices.Tool/Eryph.GuestServices.Tool.csproj" `
    -c Release -r win-x64 --nologo
}

$publishPath = Resolve-Path "$PSScriptRoot/../../src/Eryph.GuestServices.Service/bin/Release/net10.0/linux-x64/publish"
$toolPublishPath = Resolve-Path "$PSScriptRoot/../../src/Eryph.GuestServices.Tool/bin/Release/net10.0-windows/win-x64/publish"

$env:Path = "$($toolPublishPath.Path);$env:Path"
Write-Host "Using egs-tool from: $toolPublishPath"
Write-Host "egs-tool version: $(egs-tool --version)"

Write-Host "Running Pester suite ..."
$container = New-PesterContainer -Path "$PSScriptRoot/Shell.Linux.E2E.Tests.ps1" `
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
$config.TestResult.OutputPath = "$PSScriptRoot/TEST-shell-linux-pesterResults.xml"

Invoke-Pester -Configuration $config
