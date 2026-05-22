#Requires -Version 7.0

<#
.SYNOPSIS
  Run the Azure VM Extension handler test suite (host-side, no VM required).

.DESCRIPTION
  Drives Pester against HandlerLib.Tests.ps1 (unit tests) and Simulator.Tests.ps1
  (simulator contract + handler-on-staged-layout integration). No real Windows
  services are mutated — all service cmdlets are mocked via Pester.

  Designed to fit alongside the existing test/e2e runners. Output format is the
  same NUnit3 XML so CI can pick it up.
#>
[CmdletBinding()]
param(
    [switch] $SkipPesterInstall
)

$ErrorActionPreference = 'Stop'

if (-not $SkipPesterInstall) {
    Write-Host "Installing Pester (5.5+) if needed ..."
    Install-Module -Name Pester -MinimumVersion 5.5 -Force -Scope CurrentUser -SkipPublisherCheck
}

Import-Module Pester -MinimumVersion 5.5 -Force

$config = New-PesterConfiguration
$config.Output.Verbosity = 'Detailed'
$config.Run.Path = $PSScriptRoot
$config.Run.Exit = $true
$config.TestResult.Enabled = $true
$config.TestResult.OutputFormat = 'NUnit3'
$config.TestResult.OutputPath = "$PSScriptRoot/TEST-azure-extension-pesterResults.xml"

Invoke-Pester -Configuration $config
