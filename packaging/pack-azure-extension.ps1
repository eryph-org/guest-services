#Requires -Version 7.0

<#
.SYNOPSIS
  Build the Azure VM Extension package for egs-service.

.DESCRIPTION
  Produces the directory layout the Azure VM Agent expects:

      eryph-guest-services-<version>.zip
        ├── HandlerManifest.json
        ├── install.cmd / enable.cmd / disable.cmd / uninstall.cmd / update.cmd
        ├── bin/
        │   ├── Handler.ps1
        │   └── HandlerLib.psm1
        └── payload/
            └── bin/<egs-service publish output>

  The handler copies payload/* to InstallRoot at install time; layout under
  payload/ mirrors what an offline `Expand-Archive` of egs-local.zip would
  drop into `C:\Program Files\eryph\guest-services\` so the install path is
  shared with the ISO/local-zip installers.

  Not signed; not submission-ready for Partner Center. See packaging/
  azure-extension/README.md for the gap list.

.PARAMETER OutputDir
  Where to drop the zip. Defaults to packaging/azure-extension/dist/.

.PARAMETER Configuration
  Build configuration. Default Release.

.PARAMETER SkipBuild
  Reuse existing publish output instead of running dotnet publish.

.PARAMETER Version
  Override the version embedded in the zip filename. Defaults to GitVersion's
  SemVer via assembly product version, or '0.0.0-dev' if no built binary is
  available.
#>
[CmdletBinding()]
param(
    [string] $OutputDir,

    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',

    [switch] $SkipBuild,

    [string] $Version
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$extensionDir = Join-Path $repoRoot 'packaging\azure-extension'
$projectPath = Join-Path $repoRoot 'src\Eryph.GuestServices.Service\Eryph.GuestServices.Service.csproj'

if (-not (Test-Path -LiteralPath $extensionDir)) {
    throw "Azure extension source not found at $extensionDir."
}
if (-not (Test-Path -LiteralPath $projectPath)) {
    throw "egs-service project not found at $projectPath — run from a guest-services repo clone."
}

if (-not $OutputDir) {
    $OutputDir = Join-Path $extensionDir 'dist'
}

$staging = Join-Path $env:TEMP "egs-azureext-$([guid]::NewGuid().ToString('N'))"
$payloadBinDir = Join-Path $staging 'payload\bin'
$null = New-Item -ItemType Directory -Path $payloadBinDir -Force

try {
    if (-not $SkipBuild) {
        Write-Host "Publishing egs-service ($Configuration, win-x64) -> $payloadBinDir"
        & dotnet publish $projectPath `
            --configuration $Configuration `
            --runtime win-x64 `
            --output $payloadBinDir `
            --nologo
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet publish failed with exit $LASTEXITCODE"
        }
    } else {
        $publishedDir = Join-Path $repoRoot "src\Eryph.GuestServices.Service\bin\$Configuration\net10.0\win-x64\publish"
        if (-not (Test-Path -LiteralPath $publishedDir)) {
            throw "SkipBuild requested but no published output at $publishedDir — drop the switch or publish first."
        }
        Write-Host "Reusing $publishedDir -> $payloadBinDir"
        Copy-Item -Recurse "$publishedDir\*" $payloadBinDir -Force
    }

    $servicePath = Join-Path $payloadBinDir 'egs-service.exe'
    if (-not (Test-Path -LiteralPath $servicePath)) {
        throw "egs-service.exe missing from $payloadBinDir — publish did not produce expected output."
    }

    if (-not $Version) {
        $info = (Get-Item -LiteralPath $servicePath).VersionInfo.ProductVersion
        if ($info -match '^(\d+\.\d+\.\d+(-[^+\s]+)?)') {
            $Version = $Matches[1]
        } else {
            $Version = '0.0.0-dev'
        }
    }

    # Handler + manifest sit at the zip root; .cmd wrappers reach into bin/.
    Copy-Item -LiteralPath (Join-Path $extensionDir 'HandlerManifest.json') -Destination $staging -Force
    # `-Path` for the *.cmd glob — -LiteralPath would not expand the wildcard.
    Copy-Item -Path (Join-Path $extensionDir '*.cmd') -Destination $staging -Force
    Copy-Item -LiteralPath (Join-Path $extensionDir 'bin') -Destination $staging -Recurse -Force

    if (-not (Test-Path -LiteralPath $OutputDir)) {
        $null = New-Item -ItemType Directory -Path $OutputDir -Force
    }
    $zipPath = Join-Path $OutputDir "eryph-guest-services-$Version.zip"

    Write-Host "Packing $zipPath"
    if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
    # Top-level of the zip MUST be the manifest + .cmd + bin/ + payload/, not
    # the staging dir name. Hence "$staging\*".
    Compress-Archive -Path "$staging\*" -DestinationPath $zipPath -Force

    $fileInfo = Get-Item -LiteralPath $zipPath
    $sha = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.Substring(0, 12)
    $size = [Math]::Round($fileInfo.Length / 1MB, 2)
    Write-Host "Done. $zipPath ($size MB, sha256:$sha...)"
}
finally {
    if (Test-Path -LiteralPath $staging) {
        Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
    }
}
