#Requires -Version 5.1
#Requires -RunAsAdministrator

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0

$savedProgressPreference = $global:ProgressPreference

try {
    $global:ProgressPreference = 'SilentlyContinue'

    $service = Get-Service eryph-guest-services -ErrorAction SilentlyContinue
    if($service) {
        $service | Stop-Service
        Remove-Item -Recurse "C:\Program Files\eryph\guest-services"
    }

    Expand-Archive -Path $PSScriptRoot\*.zip -DestinationPath "C:\Program Files\eryph\guest-services"

    if(-not $service) {
        $null = sc.exe create eryph-guest-services start=auto binpath="C:\Program Files\eryph\guest-services\bin\egs-service.exe"
        $null = sc.exe failure eryph-guest-services reset=60 actions=restart/10000
        $null = sc.exe start eryph-guest-services
    } else{
        $service | Start-Service
    }
} finally {
    $global:ProgressPreference = $savedProgressPreference
}
