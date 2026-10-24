namespace Eryph.GuestServices.Provisioning.Windows;

/// <summary>
/// The PowerShell scripts the winget / chocolatey paths run. These are pure
/// string factories with no Windows API surface, kept out of the
/// Windows-only <see cref="WindowsOs"/> so unit tests can assert their shape
/// on any host without a CA1416 platform-compatibility warning.
/// </summary>
internal static class PackageScripts
{
    /// <summary>winget provisioning: OS gate + download latest release + provision for all users.</summary>
    public static string WingetProvision() =>
        """
        [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new()
        $ErrorActionPreference = 'Stop'
        $ProgressPreference = 'SilentlyContinue'

        if ([System.Net.ServicePointManager]::SecurityProtocol -ne 0) {
            [System.Net.ServicePointManager]::SecurityProtocol = [System.Net.ServicePointManager]::SecurityProtocol -bor 3072
        }

        $os = Get-CimInstance -ClassName Win32_OperatingSystem
        $build = [int]$os.BuildNumber
        $isServer = $os.ProductType -ne 1
        # The current App Installer package cannot be provisioned on Server 2019
        # (DISM rejects its manifest); clients need Windows 10 1809 or later.
        if (($isServer -and $build -lt 20348) -or $build -lt 17763) {
            throw "winget is not supported on $($os.Caption) (build $build). Supported: Windows 10 1809+, Windows 11, Windows Server 2022+."
        }

        $arch = switch ($env:PROCESSOR_ARCHITECTURE) {
            'AMD64' { 'x64' }
            'ARM64' { 'arm64' }
            default { 'x86' }
        }

        $release = Invoke-RestMethod -Uri 'https://api.github.com/repos/microsoft/winget-cli/releases/latest' -UseBasicParsing
        function Get-ReleaseAsset([string]$pattern) {
            $asset = $release.assets | Where-Object { $_.name -like $pattern } | Select-Object -First 1
            if (-not $asset) { throw "winget release $($release.tag_name) has no asset matching '$pattern'" }
            return $asset
        }

        $bundleVersion = $release.tag_name -replace '^v', ''
        $existing = Get-AppxProvisionedPackage -Online | Where-Object DisplayName -eq 'Microsoft.DesktopAppInstaller'
        Write-Host "winget release: $bundleVersion; provisioned App Installer: $(if ($existing) { $existing.Version } else { 'none' })"

        # Unpredictable temp dir: under LocalSystem $env:TEMP is C:\Windows\Temp,
        # where standard users can create but not list — a GUID name they cannot
        # discover prevents a pre-create / swap (TOCTOU) of the extraction dir.
        $tempDir = Join-Path $env:TEMP ('egs-winget-' + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
        try {
            $bundlePath = Join-Path $tempDir 'Microsoft.DesktopAppInstaller.msixbundle'
            $dependenciesZip = Join-Path $tempDir 'dependencies.zip'
            $licensePath = Join-Path $tempDir 'license.xml'

            Write-Host "Downloading winget $bundleVersion..."
            Invoke-WebRequest -Uri (Get-ReleaseAsset 'Microsoft.DesktopAppInstaller_8wekyb3d8bbwe.msixbundle').browser_download_url -OutFile $bundlePath -UseBasicParsing
            Invoke-WebRequest -Uri (Get-ReleaseAsset 'DesktopAppInstaller_Dependencies.zip').browser_download_url -OutFile $dependenciesZip -UseBasicParsing
            Invoke-WebRequest -Uri (Get-ReleaseAsset '*_License1.xml').browser_download_url -OutFile $licensePath -UseBasicParsing

            Expand-Archive -Path $dependenciesZip -DestinationPath (Join-Path $tempDir 'dependencies') -Force
            $dependencies = @(Get-ChildItem (Join-Path $tempDir "dependencies\$arch") -Filter *.appx | ForEach-Object FullName)
            if ($dependencies.Count -eq 0) {
                throw "The winget dependencies contain no packages for $arch"
            }

            Write-Host "Provisioning winget for all users..."
            Add-AppxProvisionedPackage -Online -PackagePath $bundlePath -DependencyPackagePath $dependencies -LicensePath $licensePath | Out-Null
        }
        finally {
            Remove-Item $tempDir -Recurse -Force -ErrorAction SilentlyContinue
        }

        $provisioned = Get-AppxProvisionedPackage -Online | Where-Object DisplayName -eq 'Microsoft.DesktopAppInstaller'
        if (-not $provisioned) {
            throw "App Installer is not provisioned after installation"
        }
        Write-Host "winget provisioned: App Installer $($provisioned.Version)."
        """;

    /// <summary>
    /// The LocalSystem wrapper written to winget-system.ps1. winget.exe cannot
    /// run as LocalSystem out of the box (0xC0000135 — missing dependency DLLs);
    /// this puts the App Installer + dependency package dirs on PATH first.
    /// </summary>
    public static string WingetWrapper() =>
        """
        # Runs winget as Local System. Written by eryph guest-services (WingetModule).
        $arch = switch ($env:PROCESSOR_ARCHITECTURE) { 'AMD64' { 'x64' } 'ARM64' { 'arm64' } default { 'x86' } }
        $apps = Join-Path $env:ProgramFiles 'WindowsApps'
        function Get-NewestPackage([string]$name) {
            Get-ChildItem $apps -Directory -Filter "$($name)_*_$($arch)__8wekyb3d8bbwe" -ErrorAction SilentlyContinue |
                Sort-Object { [version](($_.Name -split '_')[1]) } | Select-Object -Last 1
        }
        $appInstaller = Get-NewestPackage 'Microsoft.DesktopAppInstaller'
        if (-not $appInstaller) { Write-Error 'winget (App Installer) is not installed'; exit 1 }
        $dependencies = @('Microsoft.VCLibs.140.00.UWPDesktop', 'Microsoft.VCLibs.140.00') + @(
            Get-ChildItem $apps -Directory -Filter "Microsoft.WindowsAppRuntime.*_$($arch)__8wekyb3d8bbwe" -ErrorAction SilentlyContinue |
                ForEach-Object { ($_.Name -split '_')[0] } | Sort-Object -Unique)
        $paths = @($appInstaller.FullName) + @($dependencies | ForEach-Object { (Get-NewestPackage $_).FullName } | Where-Object { $_ })
        $env:PATH = ($paths -join ';') + ';' + $env:PATH
        & (Join-Path $appInstaller.FullName 'winget.exe') @args
        exit $LASTEXITCODE
        """;

    /// <summary>
    /// The Chocolatey bootstrap (community install.ps1). Exits 3010 when the
    /// installer reports a .NET Framework reboot is required (choco is unusable
    /// until after it).
    /// </summary>
    public static string ChocolateyBootstrap() =>
        """
        [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new()
        $ErrorActionPreference = 'Stop'
        Set-ExecutionPolicy Bypass -Scope Process -Force
        [System.Net.ServicePointManager]::SecurityProtocol = [System.Net.ServicePointManager]::SecurityProtocol -bor 3072

        # install.ps1 extracts and dot-sources tools\chocolateyInstall.ps1 from
        # $env:TEMP\chocolatey. Under LocalSystem that is C:\Windows\Temp, where a
        # standard user can create dirs — a classic TOCTOU to SYSTEM. Point TEMP
        # at an unpredictable GUID dir (users cannot list C:\Windows\Temp, so the
        # name is undiscoverable) created by us for this run.
        $tmp = Join-Path $env:TEMP ('egs-choco-' + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $tmp -Force | Out-Null
        $env:TEMP = $tmp
        $env:TMP = $tmp

        # Merge ALL streams (*>&1): the installer reports the .NET Framework
        # reboot via Write-Warning / Write-Host, which 2>&1 (error stream only)
        # would miss. Accumulate INCREMENTALLY so that if the installer prints
        # the reboot notice and only THEN hits a terminating error, the notice
        # is still in $out (a plain `$out = (... | Out-String)` would be replaced
        # by just the error record in the catch).
        $out = ''
        $failed = $false
        try {
            & { Invoke-Expression ((New-Object System.Net.WebClient).DownloadString('https://community.chocolatey.org/install.ps1')) } *>&1 |
                ForEach-Object { $out += ($_ | Out-String) }
        }
        catch {
            $out += ($_ | Out-String)
            $failed = $true
        }
        Write-Host $out

        if ($out -match 'reboot is required') {
            exit 3010
        }
        if ($failed) {
            throw "Chocolatey install failed: $out"
        }
        # choco.exe presence is verified by the caller (honouring %ChocolateyInstall%).
        """;
}
