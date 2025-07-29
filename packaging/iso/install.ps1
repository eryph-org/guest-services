#Requires -Version 5.1
#Requires -RunAsAdministrator

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0

Expand-Archive -Path $PSScriptRoot\*.zip -DestinationPath "C:\Program Files\eryph\guest-services" -ProgressAction SilentlyContinue

$null = sc.exe create eryph-guest-services binpath="C:\Program Files\eryph\guest-services\bin\egs-service.exe"
$null = sc.exe failure eryph-guest-services reset=60 actions=restart/10000
$null = sc.exe start eryph-guest-services
