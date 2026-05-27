#Requires -Version 7.4
#Requires -RunAsAdministrator

<#
.SYNOPSIS
  Runs the OpenStack metadata-service e2e against a real eryph deployment.

.DESCRIPTION
  Publishes egs-service (Release/win-x64) and egs-openstack-sim
  (Release/linux-x64, self-contained), then drives OpenStack.E2E.Tests.ps1
  which:
    - Applies the two-network config (default + metadata@169.254.169.254)
    - Deploys an Ubuntu simulator catlet running egs-openstack-sim, serving the
      captured config-2 fixture tree (test/fixtures/configdrive-openstack)
    - Creates a Windows guest catlet, and before first boot: bakes in the local
      egs-service, disables cloudbase-init, sets the SMBIOS chassis asset tag to
      "OpenStack Nova", and pins the datasource list to ["OpenStack"]
    - Asserts the guest provisioned from the HTTP metadata service (instance id
      + user-data attributable to the simulator)

  Requires Administrator (Mount-VHD, offline registry edits, Hyper-V WMI).
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

  Write-Host "Publishing egs-openstack-sim for linux-x64 (self-contained) ..."
  dotnet publish "$PSScriptRoot/../Eryph.GuestServices.OpenStackMetadataSim/Eryph.GuestServices.OpenStackMetadataSim.csproj" `
    -c Release -r linux-x64 --self-contained -o `
    "$PSScriptRoot/../Eryph.GuestServices.OpenStackMetadataSim/bin/Release/net10.0/linux-x64/publish" --nologo
}

$publishPath = Resolve-Path "$PSScriptRoot/../../src/Eryph.GuestServices.Service/bin/Release/net10.0/win-x64/publish"
$simPublishPath = Resolve-Path "$PSScriptRoot/../Eryph.GuestServices.OpenStackMetadataSim/bin/Release/net10.0/linux-x64/publish"

Write-Host "Running OpenStack Pester suite ..."
$container = New-PesterContainer -Path "$PSScriptRoot/OpenStack.E2E.Tests.ps1" `
  -Data @{
    OSVersion = $OSVersion
    PublishPath = $publishPath.Path
    SimPublishPath = $simPublishPath.Path
  }

$config = New-PesterConfiguration
$config.Output.Verbosity = 'Detailed'
$config.Run.Container = $container
$config.Run.Exit = $true
$config.TestResult.Enabled = $true
$config.TestResult.OutputFormat = 'NUnit3'
$config.TestResult.OutputPath = "$PSScriptRoot/TEST-openstack-pesterResults.xml"

Invoke-Pester -Configuration $config
