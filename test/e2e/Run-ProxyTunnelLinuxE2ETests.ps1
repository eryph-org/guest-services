#Requires -Version 7.4
<#
.SYNOPSIS
  Runs the proxied-tunnel (`egs-tool catlet tunnel`) e2e tests against a fresh
  eryph Ubuntu catlet, over the eryph-authorized channel.

.DESCRIPTION
  Publishes egs-service linux-x64 and egs-tool win-x64, then runs the Pester
  suite. The suite provisions a catlet, swaps in the local egs-service build,
  turns PortForwardingEnabled on, authorizes this operator's managed key via
  `egs-tool catlet add-key`, and exercises egress, ingress, idle-survival and
  the fail-closed gate. Catlet + project are torn down unless $env:EGS_E2E_KEEP_VM.

  Requires: elevated PowerShell 7.4+, a running eryph with `egs-tool initialize`
  done and an eryph connection configured (the same one the eryph CLI uses),
  Pester 5.x, Eryph.ComputeClient, and the dbosoft starter + linux-starter genes.
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

Write-Host "Running Pester suite ..."
$container = New-PesterContainer -Path "$PSScriptRoot/ProxyTunnel.Linux.E2E.Tests.ps1" `
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
$config.TestResult.OutputPath = "$PSScriptRoot/TEST-proxytunnel-linux-pesterResults.xml"

Invoke-Pester -Configuration $config
