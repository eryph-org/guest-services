#Requires -Version 7.4
#Requires -RunAsAdministrator

<#
.SYNOPSIS
  Runs the packages: umbrella e2e test against a fresh base catlet.

.DESCRIPTION
  Publishes egs-service (Release/win-x64), then drives Packages.E2E.Tests.ps1:
  a single packages: list routes a bare name to winget and a chocolatey group to
  Chocolatey; the test asserts both landed on a real catlet.

  Requires Administrator (Mount-VHD + offline registry edits) and outbound
  internet in the guest.
#>
param (
    [Parameter()]
    [ValidateSet('winsrv2022', 'winsrv2025')]  # winget requires Server 2022+ (build 20348+)
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
}

$publishPath = Resolve-Path "$PSScriptRoot/../../src/Eryph.GuestServices.Service/bin/Release/net10.0/win-x64/publish"

Write-Host "Running Packages Pester suite ..."
$container = New-PesterContainer -Path "$PSScriptRoot/Packages.E2E.Tests.ps1" `
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
$config.TestResult.OutputPath = "$PSScriptRoot/TEST-packages-pesterResults.xml"

Invoke-Pester -Configuration $config
