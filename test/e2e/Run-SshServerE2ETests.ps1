#Requires -Version 7.4
#Requires -RunAsAdministrator

<#
.SYNOPSIS
  Runs the built-in ssh-server (running + Automatic) e2e test against a fresh
  base catlet.

.DESCRIPTION
  Publishes egs-service (Release/win-x64), then drives SshServer.E2E.Tests.ps1
  which deploys a catlet with `ssh.install_openssh: true` and asserts the sshd
  service ends up Running and set to start Automatically (and is re-enabled when
  found at Manual/Stopped). Requires Administrator (Mount-VHD + offline registry
  edit).
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
}

$publishPath = Resolve-Path "$PSScriptRoot/../../src/Eryph.GuestServices.Service/bin/Release/net10.0/win-x64/publish"

Write-Host "Running SshServer Pester suite ..."
$container = New-PesterContainer -Path "$PSScriptRoot/SshServer.E2E.Tests.ps1" `
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
$config.TestResult.OutputPath = "$PSScriptRoot/TEST-sshserver-pesterResults.xml"

Invoke-Pester -Configuration $config
