#ps1_sysnative
# Sample 07 — A Windows PowerShell user-data payload.
#
# Exercises:
#   - UserDataContentTypeSniffer recognises the #ps1_sysnative marker and
#     classifies the payload as text/x-shellscript with ScriptKind.PowerShell.
#   - ScriptsUserModule writes the script under the per-instance scripts
#     directory and invokes powershell.exe with -File.
#
# Expected provisioning outcome:
#   - One marker file at C:\ProgramData\eryph-samples\07\ran.marker containing the
#     current timestamp.
#   - No cloud-config keys are processed (this is a raw shellscript payload,
#     not a #cloud-config document).
$ErrorActionPreference = 'Stop'
$markerDir = 'C:\ProgramData\eryph-samples\07'
New-Item -ItemType Directory -Force -Path $markerDir | Out-Null
Set-Content -LiteralPath (Join-Path $markerDir 'ran.marker') -Value (Get-Date -Format o)
Write-Host "Sample 07 PowerShell user-data executed at $(Get-Date -Format o)"
