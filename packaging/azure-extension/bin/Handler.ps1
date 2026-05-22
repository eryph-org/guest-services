#Requires -Version 5.1

# Thin dispatcher invoked by the .cmd lifecycle wrappers. All real logic lives
# in HandlerLib.psm1 so it can be unit-tested without the .cmd indirection.

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Install', 'Enable', 'Disable', 'Uninstall')]
    [string] $Operation
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0

Import-Module (Join-Path $PSScriptRoot 'HandlerLib.psm1') -Force

$code = Invoke-HandlerOperation -Operation $Operation -ScriptRoot $PSScriptRoot
exit $code
