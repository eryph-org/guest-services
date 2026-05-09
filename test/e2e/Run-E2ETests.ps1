#Requires -Version 7.4
<#
.SYNOPSIS
  Runs the configurable-shell e2e tests against a fresh eryph catlet.

.DESCRIPTION
  Builds the egs-service in Release/win-x64, spins up a Windows VM via eryph,
  patches the gene-installed service with the locally-built binaries, then
  runs the Pester suite. Tear-down (catlet + project) happens in AfterAll.

  Requires:
    - PowerShell 7.4+
    - eryph host with `egs-tool initialize` already run
    - Pester 5.x, Eryph.ComputeClient module
    - Hyper-V VM with the `dbosoft/$OSVersion-standard/starter` parent gene
      cached locally
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

# Use the locally-built egs-tool for this session — set-shell does not exist
# in older system installs.
$env:Path = "$($toolPublishPath.Path);$env:Path"
Write-Host "Using egs-tool from: $toolPublishPath"
Write-Host "egs-tool version: $(egs-tool --version)"

Write-Host "Running Pester suite ..."
$container = New-PesterContainer -Path "$PSScriptRoot/Shell.E2E.Tests.ps1" `
  -Data @{ OSVersion = $OSVersion; PublishPath = $publishPath.Path }

$config = New-PesterConfiguration
$config.Output.Verbosity = 'Detailed'
$config.Run.Container = $container
$config.Run.Exit = $true
$config.TestResult.Enabled = $true
$config.TestResult.OutputFormat = 'NUnit3'
$config.TestResult.OutputPath = "$PSScriptRoot/TEST-pesterResults.xml"

Invoke-Pester -Configuration $config
