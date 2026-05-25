#Requires -Version 7.0

<#
.SYNOPSIS
  Build egs-service for win-x64 and drop a Chef-cookbook-compatible
  egs-local.zip into a hyperv-boxes catlet template.

.DESCRIPTION
  Mirrors the CI pack step (azure-pipelines.yml lines ~50-66): publish into
  a staging dir with a 'bin' suffix, then archive WITHOUT the staging dir
  prefix — giving a zip whose top-level is 'bin/egs-service.exe + deps'.
  That is exactly what the hyperv-boxes eryph.rb Chef recipe expects after
  `Expand-Archive ... -DestinationPath "C:\Program Files\eryph\guest-services"`.

  The default output directory is the windows cookbook files dir of the
  hyperv-boxes repo, so the produced zip is picked up by the recipe on the
  next Packer build. The recipe prefers `egs-local.zip` over the released
  download when present.

  For testing only — the official artifact comes from CI.

.PARAMETER OutputDir
  Where to drop egs-local.zip. Defaults to the hyperv-boxes cookbook files
  dir; override for ad-hoc use.

.PARAMETER Configuration
  Build configuration. Default Release.

.PARAMETER SkipBuild
  Reuse an existing publish output instead of running dotnet publish again.

.EXAMPLE
  .\packaging\pack-local-windows.ps1

.EXAMPLE
  .\packaging\pack-local-windows.ps1 -OutputDir C:\Temp\egs-builds
#>
[CmdletBinding()]
param(
    [string] $OutputDir = 'S:\eryph\hyperv-boxes\templates\windows\cookbooks\packer\files\default',

    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',

    [switch] $SkipBuild
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot 'src\Eryph.GuestServices.Service\Eryph.GuestServices.Service.csproj'
if (-not (Test-Path -LiteralPath $projectPath)) {
    throw "egs-service project not found at $projectPath — run from a guest-services repo clone."
}

$staging = Join-Path $env:TEMP "egs-local-pack-$([guid]::NewGuid().ToString('N'))"
$null = New-Item -ItemType Directory -Path $staging -Force
$binDir = Join-Path $staging 'bin'
$null = New-Item -ItemType Directory -Path $binDir -Force

try {
    if (-not $SkipBuild) {
        Write-Host "Publishing egs-service ($Configuration, win-x64) -> $binDir"
        & dotnet publish $projectPath `
            --configuration $Configuration `
            --runtime win-x64 `
            --output $binDir `
            --nologo
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet publish failed with exit $LASTEXITCODE"
        }
    } else {
        # Copy from the standard publish output without rebuilding.
        $publishedDir = Join-Path $repoRoot "src\Eryph.GuestServices.Service\bin\$Configuration\net10.0\win-x64\publish"
        if (-not (Test-Path -LiteralPath $publishedDir)) {
            throw "SkipBuild requested but no published output at $publishedDir — drop the switch or publish first."
        }
        Write-Host "Reusing $publishedDir -> $binDir"
        Copy-Item -Recurse "$publishedDir\*" $binDir -Force
    }

    if (-not (Test-Path -LiteralPath (Join-Path $binDir 'egs-service.exe'))) {
        throw "egs-service.exe missing from $binDir — publish did not produce the expected output."
    }

    if (-not (Test-Path -LiteralPath $OutputDir)) {
        $null = New-Item -ItemType Directory -Path $OutputDir -Force
    }
    $zipPath = Join-Path $OutputDir 'egs-local.zip'

    Write-Host "Packing $zipPath"
    # IMPORTANT: -Path "$staging\*" (not $staging) so the zip's top-level
    # is 'bin/<contents>' rather than '<stagingname>/bin/<contents>'.
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
