#Requires -Version 5.1
#Requires -RunAsAdministrator

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0

Expand-Archive -Path $PSScriptRoot\*.zip -DestinationPath "C:\Program Files\eryph\guest-services"

sc.exe create eryph-guest-services binpath="C:\Program Files\eryph\guest-services\bin\egs-service.exe"
sc.exe failure eryph-guest-services reset=60 actions=restart/10000
sc.exe start eryph-guest-services
