#Requires -Version 7.4

# Adapted from MaxBack/test/e2e/Helpers.ps1.

# egs ssh ALWAYS has to use the Windows OpenSSH client. A bare `ssh.exe`
# can resolve to Git-Bash's portable OpenSSH (in C:\Program Files\Git\usr\bin
# on most dev boxes), which:
#   - reads ~/.ssh/config as a Unix path and corrupts Windows-style Include
#     directives ("/c/Users/<u>/.ssh/<C:\Users\...>") — so the catlet alias
#     blocks are never applied and the hostname falls through to DNS;
#   - uses MSYS2-bundled libcrypto which fails to load private keys whose
#     ACL only grants BUILTIN\Administrators (the egs id_egs files).
# Pinning to %WINDIR%\System32\OpenSSH\ssh.exe guarantees the same client
# the docs and ssh_config generation target.
$script:WinSshExe = Join-Path $env:WINDIR 'System32\OpenSSH\ssh.exe'
$script:WinScpExe = Join-Path $env:WINDIR 'System32\OpenSSH\scp.exe'
foreach ($exe in @($script:WinSshExe, $script:WinScpExe)) {
  if (-not (Test-Path -LiteralPath $exe)) {
    throw "Windows OpenSSH binary not found at $exe; install the 'OpenSSH.Client' optional feature."
  }
}

function New-ThrowawayPassword {
  <#
  .SYNOPSIS
    Generates a strong random password for a throwaway test account.
    Never persisted past the catlet's AfterAll teardown. Generated rather
    than hardcoded so secret scanners (GitGuardian etc.) don't flag the
    source. Meets the Windows default complexity policy (upper + lower +
    digit + symbol; >= 12 chars; avoids the obvious account-name overlap
    traps from the Provisioning.E2E catlet comments).
  #>
  [CmdletBinding()]
  param([int] $Length = 18)

  $upper  = [char[]] 'ABCDEFGHJKLMNPQRSTUVWXYZ'    # excludes I, O
  $lower  = [char[]] 'abcdefghjkmnpqrstuvwxyz'     # excludes i, l, o
  $digits = [char[]] '23456789'                    # excludes 0, 1
  $sym    = [char[]] '!@#$%^*-_=+?'

  $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
  try {
    function Pick($pool) {
      $bytes = [byte[]]::new(4)
      $rng.GetBytes($bytes)
      $idx = [BitConverter]::ToUInt32($bytes, 0) % [uint32]$pool.Length
      $pool[$idx]
    }
    # Seed one of each category, then fill with the union and shuffle.
    $chars = @((Pick $upper), (Pick $lower), (Pick $digits), (Pick $sym))
    $pool = $upper + $lower + $digits + $sym
    while ($chars.Count -lt $Length) { $chars += Pick $pool }
    # Fisher-Yates with the same RNG.
    for ($i = $chars.Count - 1; $i -gt 0; $i--) {
      $bytes = [byte[]]::new(4)
      $rng.GetBytes($bytes)
      $j = [int]([BitConverter]::ToUInt32($bytes, 0) % [uint32]($i + 1))
      $tmp = $chars[$i]; $chars[$i] = $chars[$j]; $chars[$j] = $tmp
    }
    -join $chars
  }
  finally {
    $rng.Dispose()
  }
}

function New-TestProject {
  # Eryph project names are capped at 20 characters.
  $projectName = "egs-$(Get-Date -Format 'yyyyMMddHHmmss')"
  New-EryphProject -Name $projectName
}

function New-CatletName {
  "egs$(Get-Date -Format 'yyMMddHHmmss')"
}

function Wait-Assert {
  [CmdletBinding()]
  param(
    [Parameter(Mandatory = $true, Position = 0)]
    [scriptblock] $Assertion,

    [Parameter()]
    [timespan] $Timeout = (New-TimeSpan -Minutes 1),

    [Parameter()]
    [timespan] $Interval = (New-TimeSpan -Seconds 5)
  )

  $cutOff = (Get-Date).Add($Timeout)
  while ($true) {
    try {
      & $Assertion
      return
    } catch {
      if ((Get-Date) -gt $cutOff) { throw }
      Write-Debug "  not yet, retrying in $($Interval.TotalSeconds)s"
    }
    Start-Sleep -Seconds $Interval.TotalSeconds
  }
}

function Connect-Catlet {
  [CmdletBinding()]
  param(
    [Parameter(Mandatory = $true)] [string] $Id,
    [Parameter()] [timespan] $Timeout = (New-TimeSpan -Minutes 10)
  )

  $PSNativeCommandUseErrorActionPreference = $true
  $ErrorActionPreference = 'Stop'

  $catlet = Get-Catlet -Id $Id
  $cutOff = (Get-Date).Add($Timeout)

  Write-Verbose "Starting catlet $($catlet.Name) (Id: $Id)..."
  while ($true) {
    try { Start-Catlet -Id $Id -Force; break }
    catch {
      if ((Get-Date) -gt $cutOff) { throw "Failed to start catlet within timeout: $_" }
      Start-Sleep -Seconds 5
    }
  }

  $success = $false
  Write-Verbose "Waiting for catlet to be ready..."
  while (-not $success) {
    try {
      $vmData = egs-tool get-data --json $catlet.VmId | ConvertFrom-Json -AsHashtable
      if (-not ($vmData.guest.Keys -like 'CLOUDBASE_INIT|0|provisioning|completed|*')) {
        throw 'cloudbase-init has not completed yet'
      }

      $egsStatus = egs-tool get-status $catlet.VmId
      if ($egsStatus -ne 'available') {
        throw "guest services not available: $egsStatus"
      }

      egs-tool add-ssh-config $catlet.VmId

      # Quick connectivity probe
      $sshProcess = Start-Process -FilePath 'ssh.exe' `
        -ArgumentList "$($catlet.VmId).hyper-v.alt hostname" `
        -Wait -PassThru -WindowStyle Hidden
      if ($sshProcess.ExitCode -ne 0) {
        throw "ssh probe failed: $($sshProcess.ExitCode)"
      }
      $success = $true
    } catch {
      if ((Get-Date) -gt $cutOff) {
        throw "Timed out waiting for catlet readiness: $_"
      }
      Write-Debug "  not ready: $_"
      Start-Sleep -Seconds 5
    }
  }
  Write-Verbose "Catlet ready"
}

function Update-EgsService {
  [CmdletBinding()]
  param(
    [Parameter(Mandatory = $true)] $Catlet,
    [Parameter(Mandatory = $true)] [string] $PublishPath
  )

  $PSNativeCommandUseErrorActionPreference = $true
  $ErrorActionPreference = 'Stop'

  if (-not (Test-Path $PublishPath)) {
    throw "Publish path not found: $PublishPath. Run 'dotnet publish' first."
  }

  Write-Verbose "Uploading service binaries from $PublishPath ..."
  egs-tool upload-directory $Catlet.VmId $PublishPath 'C:\egs-staging' --overwrite --recursive

  # Deploy script: stop service, copy files, start. Runs detached via scheduled
  # task so the SSH session that triggers it can disconnect cleanly when the
  # service stops (the service IS our SSH server).
  $deployScript = @'
$ErrorActionPreference = 'Stop'
$logPath = 'C:\egs-staging\deploy.log'
$src = 'C:\egs-staging'
$dst = 'C:\Program Files\eryph\guest-services\bin'
try {
  "[$(Get-Date -Format o)] stopping service" | Out-File -Append $logPath
  Stop-Service -Name eryph-guest-services -Force
  # Status can flip to Stopped while the process is still releasing its DLLs.
  # Wait until the process is actually gone before touching the bin directory.
  $sw = [Diagnostics.Stopwatch]::StartNew()
  while (
    (Get-Service eryph-guest-services).Status -ne 'Stopped' `
    -or (Get-Process -Name egs-service -ErrorAction SilentlyContinue)
  ) {
    if ($sw.Elapsed.TotalSeconds -gt 60) { throw 'service did not fully stop in 60s' }
    Start-Sleep -Milliseconds 500
  }
  "[$(Get-Date -Format o)] service process gone after $([int]$sw.Elapsed.TotalSeconds)s" | Out-File -Append $logPath
  "[$(Get-Date -Format o)] copying files (recursive, mirror $src -> $dst)" | Out-File -Append $logPath
  Get-ChildItem $src -Recurse -File `
    | Where-Object { $_.FullName -notlike "$src\deploy.ps1" -and $_.FullName -notlike "$src\deploy.log" } `
    | ForEach-Object {
        $relative = $_.FullName.Substring($src.Length).TrimStart('\')
        $target = Join-Path $dst $relative
        $targetDir = Split-Path $target -Parent
        if (-not (Test-Path -LiteralPath $targetDir)) {
          New-Item -ItemType Directory -Path $targetDir -Force | Out-Null
        }
        Copy-Item -LiteralPath $_.FullName -Destination $target -Force
      }
  "[$(Get-Date -Format o)] starting service" | Out-File -Append $logPath
  Start-Service -Name eryph-guest-services
  "[$(Get-Date -Format o)] done" | Out-File -Append $logPath
} catch {
  "[$(Get-Date -Format o)] FAILED: $_" | Out-File -Append $logPath
  throw
}
'@
  $tempScript = [System.IO.Path]::Combine([System.IO.Path]::GetTempPath(), "egs-deploy-$([guid]::NewGuid()).ps1")
  Set-Content -Path $tempScript -Value $deployScript -Encoding utf8
  try {
    egs-tool upload-file $Catlet.VmId $tempScript 'C:\egs-staging\deploy.ps1' --overwrite
  } finally {
    Remove-Item -LiteralPath $tempScript -Force
  }

  Write-Verbose "Triggering detached deploy task ..."
  $hostName = "$($Catlet.VmId).hyper-v.alt"
  ssh.exe -o StrictHostKeyChecking=no $hostName `
    'schtasks.exe /create /tn EgsDeploy /tr "powershell.exe -ExecutionPolicy Bypass -File C:\egs-staging\deploy.ps1" /sc once /st 23:59:59 /ru SYSTEM /f' | Out-Null
  ssh.exe -o StrictHostKeyChecking=no $hostName 'schtasks.exe /run /tn EgsDeploy' | Out-Null

  Write-Verbose "Waiting for service to come back ..."
  Start-Sleep -Seconds 5
  try {
    Wait-Assert -Timeout (New-TimeSpan -Minutes 5) {
      (egs-tool get-status $Catlet.VmId) | Should -Be 'available'
    }
  } catch {
    Write-Host "Service did not come back. VM=$($Catlet.Name) (VmId=$($Catlet.VmId))"
    Write-Host "Inspect via Hyper-V Manager -> $($Catlet.Name) -> Connect."
    Write-Host "Deploy log lives at C:\egs-staging\deploy.log inside the VM."
    throw
  }
  # Confirm SSH is responsive.
  Wait-Assert -Timeout (New-TimeSpan -Seconds 30) {
    $sshProcess = Start-Process -FilePath 'ssh.exe' `
      -ArgumentList "$hostName hostname" -Wait -PassThru -WindowStyle Hidden
    $sshProcess.ExitCode | Should -Be 0
  }
}

function Get-CatletVhdPath {
  <#
  .SYNOPSIS
    Returns the path of the OS VHD attached to a catlet.

  .DESCRIPTION
    eryph delegates to Hyper-V; the catlet's VmId is the Hyper-V Vm.Id. We
    take the first VMHardDiskDrive, which for a catlet built from a starter
    parent is the boot/OS disk. If a catlet ever ships with multiple disks
    we should be smarter — flag for v2.
  #>
  [CmdletBinding()]
  param(
    [Parameter(Mandatory = $true)] [Guid] $VmId
  )

  $vm = Get-VM -Id $VmId -ErrorAction Stop
  $drive = (Get-VMHardDiskDrive -VM $vm)[0]
  if (-not $drive) { throw "Catlet $VmId has no VMHardDiskDrive" }
  return $drive.Path
}

function Mount-CatletVhd {
  <#
  .SYNOPSIS
    Mounts the catlet's VHD on the host and returns the Windows-volume drive
    letter (e.g. 'E') that contains the guest's C:\.

  .DESCRIPTION
    Mounts the catlet's CHILD differencing VHD (returned by Get-VMHardDiskDrive).
    Hyper-V resolves the chain automatically so the joined parent + child view
    is what we read/write; copy-on-write keeps the parent intact.

    Returns the volume root as a Volume GUID path (e.g. \\?\Volume{<guid>}\)
    rather than a drive letter — no Add-PartitionAccessPath / drive-letter
    assignment, no host registry mutation, nothing to clean up. The catlet
    must NOT be running (Hyper-V holds the lock otherwise).

    Requires administrator (Mount-VHD needs Hyper-V management rights).
  #>
  [CmdletBinding()]
  param(
    [Parameter(Mandatory = $true)] [Guid] $VmId
  )

  $vhdPath = Get-CatletVhdPath -VmId $VmId
  Write-Verbose "Mounting catlet VHD: $vhdPath"
  $disk = Mount-VHD -Path $vhdPath -PassThru -ErrorAction Stop
  $disk = Get-Disk -Number $disk.Number

  try {
    # Walk the partitions and find the one whose volume root contains
    # \Windows. Volume.Path is the \\?\Volume{GUID}\ form, always populated
    # for a mounted NTFS/ReFS volume even without a drive letter.
    foreach ($p in (Get-Partition -DiskNumber $disk.Number)) {
      $vol = $p | Get-Volume -ErrorAction SilentlyContinue
      if (-not $vol) { continue }
      if ($vol.FileSystem -notin 'NTFS', 'ReFS') { continue }
      if (-not $vol.Path) { continue }

      $volumeRoot = $vol.Path
      if (Test-Path -LiteralPath (Join-Path $volumeRoot 'Windows')) {
        Write-Verbose "Found OS partition at $volumeRoot"
        return [pscustomobject]@{
          VhdPath    = $vhdPath
          VolumeRoot = $volumeRoot
        }
      }
    }

    # No match — dump partition state so the failure is debuggable.
    $summary = (Get-Partition -DiskNumber $disk.Number) | ForEach-Object {
      $vol = $_ | Get-Volume -ErrorAction SilentlyContinue
      "  #$($_.PartitionNumber) Size=$($_.Size) FS=$($vol.FileSystem) VolumePath=$($vol.Path) Type=$($_.Type)"
    }
    throw "Could not find a Windows OS partition on $vhdPath. Partitions:`n$($summary -join "`n")"
  }
  catch {
    Dismount-VHD -Path $vhdPath -ErrorAction SilentlyContinue
    throw
  }
}

function Dismount-CatletVhd {
  [CmdletBinding()]
  param(
    [Parameter(Mandatory = $true)] [string] $VhdPath
  )
  Write-Verbose "Dismounting $VhdPath"
  Dismount-VHD -Path $VhdPath -ErrorAction Stop
}

function Update-EgsServiceBinariesOffline {
  <#
  .SYNOPSIS
    Replaces the egs-service binaries inside a stopped catlet's VHD with the
    locally-built publish output, and disables cloudbase-init.

  .DESCRIPTION
    The parent gene already installed egs-service into
    `C:\Program Files\eryph\guest-services\bin\` and registered it as a
    Windows service — we just overwrite the files. This requires admin
    (Mount-VHD + offline reg edit for cbi disable).

  .PARAMETER VmId
    The catlet's VmId. The catlet must be Off (not Running, not Saved).

  .PARAMETER PublishPath
    Directory containing the `dotnet publish` output of egs-service. Must
    include `egs-service.exe`.

  .NOTES
    Operates ONLY on the offline VHD — does not touch the host.
  #>
  [CmdletBinding()]
  param(
    [Parameter(Mandatory = $true)] [Guid] $VmId,
    [Parameter(Mandatory = $true)] [string] $PublishPath
  )

  if (-not (Test-Path -LiteralPath $PublishPath)) {
    throw "PublishPath not found: $PublishPath. Run 'dotnet publish' first."
  }
  if (-not (Test-Path -LiteralPath (Join-Path $PublishPath 'egs-service.exe'))) {
    throw "egs-service.exe not present under $PublishPath."
  }

  $status = (Get-VM -Id $VmId).State
  if ($status -eq 'Running' -or $status -eq 'Saved') {
    throw "Catlet must be Stopped (current: $status) before mounting its VHD."
  }

  $mount = Mount-CatletVhd -VmId $VmId
  try {
    $root = $mount.VolumeRoot
    $binDir = Join-Path $root 'Program Files\eryph\guest-services\bin'
    $cbiDir = Join-Path $root 'Program Files\Cloudbase Solutions\Cloudbase-Init'

    if (-not (Test-Path -LiteralPath $binDir)) {
      throw "Expected egs-service install at $binDir but the directory is missing. " +
            "The parent gene should have egs-service pre-installed; without it this " +
            "test cannot just replace binaries — it would need to install the service " +
            "too. Verify the parent gene or pick one with egs-service baked in."
    }

    Write-Verbose "Replacing egs-service binaries in $binDir"
    # Mirror copy: drop new file contents on top of the existing layout.
    Get-ChildItem -LiteralPath $PublishPath -Recurse -File | ForEach-Object {
      $relative = $_.FullName.Substring($PublishPath.Length).TrimStart('\','/')
      $target = Join-Path $binDir $relative
      $targetDir = Split-Path $target -Parent
      if (-not (Test-Path -LiteralPath $targetDir)) {
        New-Item -ItemType Directory -Path $targetDir -Force | Out-Null
      }
      Copy-Item -LiteralPath $_.FullName -Destination $target -Force
    }

    # Three places cbi can be invoked from. We neuter all three:
    #
    #   (1) The cbi install directory — renamed so the binaries are unreachable.
    #   (2) The cbi Windows service — Start=Disabled in the offline SYSTEM hive
    #       so the SCM doesn't try to spawn a service whose binary just moved.
    #   (3) Sysprep's unattend.xml — RunSynchronousCommand entries that invoke
    #       cbi during OOBE specialize. If we don't patch these, OOBE runs
    #       cbi.exe at the renamed path, gets a non-zero exit code, and halts
    #       before egs-service ever starts.
    if (Test-Path -LiteralPath $cbiDir) {
      $disabledName = "$cbiDir.disabled-$(Get-Date -Format 'yyyyMMddHHmmss')"
      Write-Verbose "Disabling cloudbase-init: $cbiDir -> $disabledName"
      Move-Item -LiteralPath $cbiDir -Destination $disabledName -Force

      Set-OfflineServiceStartType -VolumeRoot $mount.VolumeRoot `
        -ServiceName 'cloudbase-init' -StartType 4   # 4 = Disabled
    } else {
      Write-Verbose "cloudbase-init not present at $cbiDir; skipping dir rename"
    }

    Disable-CloudbaseInitUnattend -VolumeRoot $mount.VolumeRoot
  }
  finally {
    Dismount-CatletVhd -VhdPath $mount.VhdPath
  }
}

function Write-OfflineEgsClientKey {
  <#
  .SYNOPSIS
    Writes a public key into the offline catlet's egs-service client-key
    cache path (C:\ProgramData\eryph\guest-services\id_egs.pub), mimicking
    what gene:dbosoft/guest-services:win-install would have done. Used in
    the offline-injection path where cloudbase-init (and the win-install
    gene that depends on it) are disabled.

  .DESCRIPTION
    The catlet must be Stopped. Mounts the VHD, creates the directory
    chain if missing, writes the supplied public-key string (single
    OpenSSH line), then dismounts. egs-service reads this file via
    WindowsKeyStorage.GetClientKeyAsync on first auth and treats it as
    the catlet's primary authorized identity.
  #>
  [CmdletBinding()]
  param(
    [Parameter(Mandatory = $true)] [Guid] $VmId,
    [Parameter(Mandatory = $true)] [string] $PublicKey
  )

  $status = (Get-VM -Id $VmId).State
  if ($status -eq 'Running' -or $status -eq 'Saved') {
    throw "Catlet must be Stopped (current: $status) before mounting its VHD."
  }

  $mount = Mount-CatletVhd -VmId $VmId
  try {
    $configDir = Join-Path $mount.VolumeRoot 'ProgramData\eryph\guest-services'
    if (-not (Test-Path -LiteralPath $configDir)) {
      New-Item -ItemType Directory -Path $configDir -Force | Out-Null
    }
    $keyFile = Join-Path $configDir 'id_egs.pub'
    # WindowsKeyStorage.GetClientKeyAsync reads with File.ReadAllText and
    # then KeyPair.ImportKey, so trailing newlines are tolerated. Match the
    # OpenSSH single-line convention with one trailing LF. UTF-8 *without*
    # BOM — File.ReadAllText auto-strips BOM, but the OpenSSH writer
    # convention is BOM-less and a few key parsers in the wild trip on it.
    $utf8NoBom = New-Object System.Text.UTF8Encoding $false
    [System.IO.File]::WriteAllText($keyFile, $PublicKey.TrimEnd() + "`n", $utf8NoBom)
    Write-Verbose "Wrote provisioned client key to $keyFile"
  }
  finally {
    Dismount-CatletVhd -VhdPath $mount.VhdPath
  }
}

function Disable-CloudbaseInitUnattend {
  <#
  .SYNOPSIS
    Replaces RunSynchronousCommand entries that invoke cloudbase-init with
    a no-op success command in every unattend.xml found in the offline image.

  .DESCRIPTION
    The parent gene's sysprep'd image references cloudbase-init from an
    OOBE/specialize unattend.xml, e.g.:

      <RunSynchronousCommand wcm:action="add">
        <Order>10</Order>
        <Path>cmd.exe /c ""C:\Program Files\Cloudbase Solutions\Cloudbase-Init\Python\Scripts\cloudbase-init.exe" --config-file "...\cloudbase-init-unattend.conf" && exit 1 || exit 2"</Path>
        ...
      </RunSynchronousCommand>

    If we only rename the cbi install dir, this command exits non-zero and
    OOBE halts. We rewrite each matching entry to `cmd.exe /c "exit 0"` and
    set WillReboot to Never. Order is preserved so other commands in the
    sequence still line up.
  #>
  [CmdletBinding()]
  param(
    [Parameter(Mandatory = $true)] [string] $VolumeRoot
  )

  $candidates = @(
    (Join-Path $VolumeRoot 'Windows\System32\Sysprep\unattend.xml'),
    (Join-Path $VolumeRoot 'Windows\Panther\unattend.xml'),
    (Join-Path $VolumeRoot 'Windows\Panther\unattend\unattend.xml'),
    (Join-Path $VolumeRoot 'unattend.xml')
  )

  foreach ($path in $candidates) {
    if (-not (Test-Path -LiteralPath $path)) { continue }

    Write-Verbose "Scanning unattend.xml for cloudbase-init RunSynchronousCommand entries: $path"
    [xml]$xml = Get-Content -LiteralPath $path

    $ns = New-Object System.Xml.XmlNamespaceManager $xml.NameTable
    $ns.AddNamespace('u', 'urn:schemas-microsoft-com:unattend')

    $modified = $false
    foreach ($node in $xml.SelectNodes('//u:RunSynchronousCommand', $ns)) {
      $pathNode = $node.SelectSingleNode('u:Path', $ns)
      if (-not $pathNode) { continue }
      if ($pathNode.InnerText -notmatch 'cloudbase-init') { continue }

      $order = $node.SelectSingleNode('u:Order', $ns).InnerText
      Write-Verbose "  Patching cbi RunSynchronousCommand at Order=$order"
      $pathNode.InnerText = 'cmd.exe /c "exit 0"'

      $descNode = $node.SelectSingleNode('u:Description', $ns)
      if ($descNode) {
        $descNode.InnerText = 'placeholder — cloudbase-init disabled by eryph e2e harness'
      }
      $willRebootNode = $node.SelectSingleNode('u:WillReboot', $ns)
      if ($willRebootNode) {
        $willRebootNode.InnerText = 'Never'
      }
      $modified = $true
    }

    if ($modified) {
      Write-Verbose "  Saving patched $path"
      $xml.Save($path)
    } else {
      Write-Verbose "  No cbi entries in $path"
    }
  }
}

function Get-CatletOsPartitionSize {
  <#
  .SYNOPSIS
    Returns the byte size of the Windows OS partition inside a stopped
    catlet's VHD, read offline via a transient Mount-VHD.

  .DESCRIPTION
    Used by the growpart e2e: we want a stable "before" measurement of the
    OS partition size so the post-boot assertion can prove the partition
    actually grew, not just that it happens to be large.

    Mirror the discovery logic from Mount-CatletVhd: walk partitions, pick
    the one whose volume root contains \Windows.
  #>
  [CmdletBinding()]
  param(
    [Parameter(Mandatory = $true)] [Guid] $VmId
  )

  $vhdPath = Get-CatletVhdPath -VmId $VmId
  Write-Verbose "Reading OS partition size from $vhdPath"
  $disk = Mount-VHD -Path $vhdPath -PassThru -ErrorAction Stop
  $disk = Get-Disk -Number $disk.Number
  try {
    foreach ($p in (Get-Partition -DiskNumber $disk.Number)) {
      $vol = $p | Get-Volume -ErrorAction SilentlyContinue
      if (-not $vol) { continue }
      if ($vol.FileSystem -notin 'NTFS', 'ReFS') { continue }
      if (-not $vol.Path) { continue }
      if (Test-Path -LiteralPath (Join-Path $vol.Path 'Windows')) {
        return [uint64] $p.Size
      }
    }
    throw "Could not locate a Windows OS partition on $vhdPath"
  }
  finally {
    Dismount-VHD -Path $vhdPath -ErrorAction SilentlyContinue
  }
}

function Set-OfflineServiceStartType {
  <#
  .SYNOPSIS
    Updates an existing service's Start value in the offline SYSTEM hive.
    Useful for disabling cloudbase-init (StartType=4) before our binary boots.
  #>
  [CmdletBinding()]
  param(
    [Parameter(Mandatory = $true)] [string] $VolumeRoot,
    [Parameter(Mandatory = $true)] [string] $ServiceName,
    [Parameter(Mandatory = $true)] [int] $StartType
  )

  $hivePath = Join-Path $VolumeRoot 'Windows\System32\config\SYSTEM'
  $tempHive = "HKLM\OfflineSystem_$([guid]::NewGuid().ToString('N'))"
  $regExe = "$env:WINDIR\System32\reg.exe"

  & $regExe load $tempHive $hivePath | Out-Null
  if ($LASTEXITCODE -ne 0) { throw "reg load failed for $hivePath" }
  try {
    $svcKey = "$tempHive\ControlSet001\Services\$ServiceName"
    # If the service doesn't exist in the hive there's nothing to disable.
    $exists = & $regExe query $svcKey 2>$null
    if ($LASTEXITCODE -eq 0) {
      & $regExe add $svcKey /f /v Start /t REG_DWORD /d $StartType | Out-Null
    }
  }
  finally {
    [System.GC]::Collect()
    [System.GC]::WaitForPendingFinalizers()
    & $regExe unload $tempHive | Out-Null
  }
}

function Install-GuestMetadataRoute {
  <#
  .SYNOPSIS
    Installs a per-boot scheduled task inside a running guest that adds an active
    host route to the OpenStack metadata IP via the overlay default gateway, and
    runs it once immediately.

  .DESCRIPTION
    The metadata IP (169.254.169.254) is link-local. Windows treats 169.254/16
    as on-link (APIPA) and never sends it to a gateway, so eryph's virtual router
    — which connects the guest's `default` overlay to the simulator's `metadata`
    overlay — is never consulted. The Linux probe (probe-catlet.yaml) only reached
    the sim because it added a `/32 via $GW` route itself; the Windows guest needs
    the same.

    A PERSISTENT route written offline does NOT work: Windows processes persistent
    routes early in boot before DHCP brings the NIC up, the link-local /32 then
    can't be installed (gateway not yet on-link), and it is never retried — so the
    route stays in the registry but never reaches the active table.

    The reliable mechanism is an ONSTART scheduled task (SYSTEM) that waits for the
    default gateway to appear, then adds the ACTIVE route. It re-runs on every boot
    so the route survives the SetHostnameModule reboot mid-provisioning. egs-service
    comes up independently and its OpenStack datasource WaitForReady-loops (up to
    DataSourceSettings.ReadinessTimeoutMinutes) until the route lands, so installing
    the task shortly after boot is well within the window.

    Requires the guest's egs SSH to be reachable (the task is registered via
    egs-tool/ssh, which lets Task Scheduler compute the task hash — avoiding the
    fragile offline TaskCache registry surgery).

  .PARAMETER MetadataIp
    The link-local metadata IP to route. Defaults to 169.254.169.254.
  #>
  [CmdletBinding()]
  param(
    [Parameter(Mandatory = $true)] [Guid] $VmId,
    [Parameter(Mandatory = $true)] $CatletId,
    [Parameter()] [string] $MetadataIp = '169.254.169.254'
  )

  $savedPref = $PSNativeCommandUseErrorActionPreference
  $PSNativeCommandUseErrorActionPreference = $false
  try {
    Write-Verbose "Waiting for egs-service before installing the metadata-route task ..."
    Wait-Assert -Timeout (New-TimeSpan -Minutes 6) -Interval (New-TimeSpan -Seconds 5) {
      $status = egs-tool get-status $VmId
      if ($status -ne 'available') { throw "guest services not available yet: $status" }
    }
    egs-tool add-ssh-config $VmId | Out-Null
    $hostName = "$VmId.hyper-v.alt"

    # Route script: wait for the overlay default gateway (DHCP), then add the
    # ACTIVE /32 route to the metadata IP via it. Idempotent — re-runs each boot.
    $routeScript = @"
`$ErrorActionPreference = 'SilentlyContinue'
`$meta = '$MetadataIp'
`$gw = `$null
for (`$i = 0; `$i -lt 60; `$i++) {
  `$gw = (Get-NetRoute -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue |
          Where-Object { `$_.NextHop -and `$_.NextHop -ne '0.0.0.0' } |
          Sort-Object RouteMetric | Select-Object -First 1).NextHop
  if (`$gw) { break }
  Start-Sleep -Seconds 2
}
if (`$gw) {
  & route.exe delete `$meta 2>`$null | Out-Null
  & route.exe add `$meta mask 255.255.255.255 `$gw metric 1 | Out-Null
}
"@
    # Register + run-now script. schtasks computes the task hash for us.
    $registerScript = @'
schtasks.exe /create /tn EgsMetadataRoute /sc onstart /ru SYSTEM /rl HIGHEST /f /tr "powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\Windows\Temp\egs-metaroute.ps1"
schtasks.exe /run /tn EgsMetadataRoute
'@

    foreach ($f in @(
      @{ Local = $routeScript;    Remote = 'C:\Windows\Temp\egs-metaroute.ps1' },
      @{ Local = $registerScript; Remote = 'C:\Windows\Temp\egs-register-metaroute.ps1' }
    )) {
      $tmp = [System.IO.Path]::GetTempFileName()
      [System.IO.File]::WriteAllText($tmp, ($f.Local -replace "`r`n", "`n"))
      try { egs-tool upload-file $VmId $tmp $f.Remote --overwrite | Out-Null }
      finally { Remove-Item -LiteralPath $tmp -Force }
    }

    Write-Verbose "Registering + running the EgsMetadataRoute task ..."
    ssh.exe -o StrictHostKeyChecking=no -o ConnectTimeout=20 $hostName `
      'powershell -NoProfile -ExecutionPolicy Bypass -File C:\Windows\Temp\egs-register-metaroute.ps1' | Out-Null

    # Confirm the route is active before we hand back to the provisioning wait.
    Wait-Assert -Timeout (New-TimeSpan -Minutes 2) -Interval (New-TimeSpan -Seconds 5) {
      $r = ssh.exe -o StrictHostKeyChecking=no $hostName "curl.exe -sS --max-time 8 http://$MetadataIp/openstack"
      if ($LASTEXITCODE -ne 0 -or "$r" -notmatch '\d{4}-\d{2}-\d{2}') {
        throw "metadata service not reachable from guest yet (out=$r)"
      }
    }
  }
  finally {
    $PSNativeCommandUseErrorActionPreference = $savedPref
  }
}

function Wait-ForProvisioningComplete {
  <#
  .SYNOPSIS
    Polls KVP for `eryph.provisioning.state` until it reads "completed",
    "failed", or a timeout expires.
  #>
  [CmdletBinding()]
  param(
    [Parameter(Mandatory = $true)] [Guid] $VmId,
    [Parameter()] [timespan] $Timeout = (New-TimeSpan -Minutes 10),
    [Parameter()] [timespan] $Interval = (New-TimeSpan -Seconds 5)
  )

  $cutOff = (Get-Date).Add($Timeout)
  while ($true) {
    try {
      $kvp = egs-tool get-data --json $VmId | ConvertFrom-Json -AsHashtable
      $state = $kvp.guest.'eryph.provisioning.state'
      if ($state -eq 'completed') {
        return $state
      }
      if ($state -eq 'failed') {
        # The failure reason now lives in the CLOUD_INIT|... FAIL event / agent.log
        # (RFC 0031), not a bespoke KVP error key. Point the operator there.
        throw "Provisioning reported failed (reason: see the CLOUD_INIT|... FAIL KVP event, agent.log, or state.json)."
      }
      Write-Verbose "Provisioning state=$state — waiting..."
    } catch {
      if ($_.Exception.Message -like 'Provisioning reported failed*') { throw }
      Write-Verbose "KVP not yet readable: $_"
    }
    if ((Get-Date) -gt $cutOff) {
      throw "Timed out waiting for provisioning to complete on $VmId."
    }
    Start-Sleep -Seconds $Interval.TotalSeconds
  }
}

function Invoke-EgsShellProbe {
  <#
  .SYNOPSIS
    Drives a shell channel against the egs SSH server using the
    `egs-shell-probe` tool and returns the captured output.

  .DESCRIPTION
    `ssh.exe -tt` cannot be redirected/captured from a script — Win32-OpenSSH
    writes channel output to the Windows console buffer, not stdout. The
    probe uses Microsoft.DevTunnels.Ssh directly (the same library the
    egs SSH server itself uses) to send pty-req, optional env requests, and
    shell, then pumps channel output to stdout. This bypasses the ssh.exe
    capture quirk while still exercising the full server-side path
    (ShellService -> PtyForwarder -> IShellSelector).
  #>
  [CmdletBinding()]
  param(
    [Parameter(Mandatory = $true)] [string] $ProbePath,
    [Parameter(Mandatory = $true)] [Guid] $VmId,
    [Parameter()] [string[]] $InputLines = @(),
    [Parameter()] [hashtable] $SetEnv,
    [Parameter()] [int] $TimeoutMs = 3000
  )

  $probeArgs = @('--vm-id', $VmId.ToString(), '--timeout-ms', $TimeoutMs.ToString())
  if ($SetEnv) {
    foreach ($k in $SetEnv.Keys) {
      $probeArgs += @('--env', "$k=$($SetEnv[$k])")
    }
  }
  foreach ($line in $InputLines) {
    $probeArgs += @('--input', $line)
  }

  $raw = & $ProbePath @probeArgs 2>&1 | Out-String
  # Strip ANSI escape sequences. The captured stream is ConPTY-encoded
  # (CSI / OSC), which is unreadable in test diagnostics and also chokes
  # Pester's NUnit3 XML serializer (ESC = 0x1B is invalid in XML 1.0).
  return $raw -replace "`e\[[0-?]*[ -/]*[@-~]", '' -replace "`e\][^`a]*`a", ''
}

function Invoke-EgsSshCommand {
  <#
  .SYNOPSIS
    Runs a single non-interactive command on the catlet via the SSH `exec`
    request (`ssh host "<command>"`) and returns its exit code and captured
    output.

  .DESCRIPTION
    Unlike the interactive shell channel, exec output is plain channel data
    that Win32-OpenSSH writes to ssh.exe's stdout, so it can be captured
    normally — no probe needed. This exercises the server-side
    CommandService -> CommandForwarder -> IShellSelector path: the command is
    run through the selected shell, and stdout + stderr are merged back onto
    the channel. stderr is captured here too (2>&1) so error-stream coverage
    works.

    Pinned to Windows OpenSSH (see $script:WinSshExe) and relies on the
    *.hyper-v.alt Host block written by `egs-tool add-ssh-config` for the
    ProxyCommand, so it must run after Connect-Catlet.
  #>
  [CmdletBinding()]
  param(
    [Parameter(Mandatory = $true)] [Guid] $VmId,
    [Parameter(Mandatory = $true)] [string] $Command,
    [Parameter()] [int] $ConnectTimeout = 15
  )

  $hostName = "$VmId.hyper-v.alt"
  $savedPref = $PSNativeCommandUseErrorActionPreference
  $PSNativeCommandUseErrorActionPreference = $false
  try {
    $out = & $script:WinSshExe `
      -o "StrictHostKeyChecking=no" `
      -o "BatchMode=yes" `
      -o "ConnectTimeout=$ConnectTimeout" `
      $hostName $Command 2>&1
    [pscustomobject]@{
      ExitCode = $LASTEXITCODE
      Output   = ($out | Out-String).Trim()
    }
  }
  finally {
    $PSNativeCommandUseErrorActionPreference = $savedPref
  }
}

function Save-GuestDiagnostics {
  <#
    .SYNOPSIS
      Pulls the provisioning agent's logs and the datasource contents off a
      running catlet into a local directory, so a failed (or passing) e2e run
      leaves a diagnosable artifact set instead of needing a manual SSH session.

    .DESCRIPTION
      Best-effort and non-throwing: diagnostics collection must never turn a
      real test failure into a cleanup error. Each probe is captured to its own
      file; a probe that fails records the error and the rest still run.

      Probe scripts use single quotes only so they survive the
      `ssh.exe "powershell -Command \"...\""` wrapping without escaping issues.
  #>
  [CmdletBinding()]
  param(
    [Parameter(Mandatory)] $CatletId,
    [Parameter(Mandatory)][string] $OutputDir
  )

  # The guest's hyper-v.alt alias is keyed on the Hyper-V VmId — the same name
  # the test's SSH probes connect to. Best-effort and non-throwing: resolve the
  # VmId and (re)write the SSH config, but never let a failure here turn a real
  # test failure into a cleanup error.
  try {
    $vmId = (Get-Catlet -Id $CatletId).VmId
    egs-tool add-ssh-config $vmId | Out-Null
  } catch {
    Write-Host "Could not prepare SSH config for diagnostics: $_"
    return
  }
  if (-not $vmId) {
    Write-Host "No VmId for catlet $CatletId; skipping guest diagnostics."
    return
  }
  $hostName = "$vmId.hyper-v.alt"
  try { New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null } catch { }
  Write-Host "Collecting guest diagnostics to $OutputDir ..."

  # Files pulled byte-for-byte over the egs VMBus channel. egs-tool download-file
  # takes the VmId and needs no SSH alias or shell, so (a) there is no
  # $-variable double-evaluation to corrupt them, (b) it works on a FAILED run
  # before add-ssh-config has even succeeded, and (c) it sidesteps scp — egs
  # exposes no SFTP subsystem, so scp against the egs server always fails.
  # agent.log is THE key diagnostic (the agent's own operational log); it was
  # previously never captured.
  $pulls = [ordered]@{
    'agent.log'       = 'C:\ProgramData\eryph\guest-services\logs\agent.log'
    'state.json'      = 'C:\ProgramData\eryph\provisioning\state.json'
    'datasource.json' = 'C:\ProgramData\eryph\provisioning\datasource.json'
  }

  # Dynamic queries that have to run in the guest. egs already runs every SSH
  # exec command through `powershell -Command "<command>"`, so we send the BARE
  # script — re-wrapping it in another `powershell -NoProfile -Command "..."`
  # makes the guest evaluate it TWICE, expanding $_/$d host-side to nothing
  # before the inner shell runs (issue #52). All scripts use single quotes only
  # so they survive intact with no embedded double quotes to break the wrap.
  #
  # The config drive is eryph's NoCloud cidata ISO: volume label 'cidata' with
  # meta-data / user-data / network-config at the root (NOT an OpenStack
  # 'config-2' volume with an \openstack\ tree).
  $cidata = '$d=(Get-Volume | Where-Object FileSystemLabel -eq ''cidata'').DriveLetter; if ($d)'
  $probes = [ordered]@{
    'provisioning-tree.txt' =
      'Get-ChildItem -Recurse -Path C:\ProgramData\eryph\provisioning -ErrorAction SilentlyContinue | Select-Object -ExpandProperty FullName'
    'eventlog.txt' =
      'Get-WinEvent -LogName Application -MaxEvents 300 -ErrorAction SilentlyContinue | Where-Object { $_.ProviderName -match ''eryph|egs'' -or $_.Message -match ''provisioning|egs-service'' } | Sort-Object TimeCreated | Format-List TimeCreated, LevelDisplayName, ProviderName, Message | Out-String'
    'configdrive-tree.txt' =
      "$cidata { Get-ChildItem -Recurse -LiteralPath (`$d + ':\') -ErrorAction SilentlyContinue | Select-Object -ExpandProperty FullName } else { 'cidata volume not found' }"
    'configdrive-meta-data.txt' =
      "$cidata { Get-Content -Raw -LiteralPath (`$d + ':\meta-data') -ErrorAction SilentlyContinue } else { 'cidata volume not found' }"
    'configdrive-network-config.txt' =
      "$cidata { Get-Content -Raw -LiteralPath (`$d + ':\network-config') -ErrorAction SilentlyContinue } else { 'cidata volume not found' }"
    'configdrive-user-data.txt' =
      "$cidata { Get-Content -Raw -LiteralPath (`$d + ':\user-data') -ErrorAction SilentlyContinue } else { 'cidata volume not found' }"
  }

  # ssh.exe non-zero exits must not abort the loop.
  $savedPref = $PSNativeCommandUseErrorActionPreference
  $PSNativeCommandUseErrorActionPreference = $false
  try {
    foreach ($name in $pulls.Keys) {
      $dest = Join-Path $OutputDir $name
      try {
        egs-tool download-file $vmId $pulls[$name] $dest 2>&1 | Out-Null
        if (-not (Test-Path -LiteralPath $dest)) {
          Set-Content -LiteralPath $dest -Value "(not present in guest: $($pulls[$name]))"
        }
      }
      catch {
        Set-Content -LiteralPath $dest -Value "download failed: $_"
      }
    }

    foreach ($name in $probes.Keys) {
      $dest = Join-Path $OutputDir $name
      try {
        # Bare script — egs wraps it in powershell -Command itself (issue #52).
        $out = ssh.exe -o StrictHostKeyChecking=no -o ConnectTimeout=10 $hostName `
          $probes[$name] 2>&1
        Set-Content -LiteralPath $dest -Value ($out | Out-String)
      }
      catch {
        Set-Content -LiteralPath $dest -Value "diagnostic collection failed: $_"
      }
    }

    # Also pull the egs-service collect-logs bundle (state + per-script logs).
    # Build it via a bare exec command, then fetch with download-file (no scp).
    try {
      $bundleCmd = "& 'C:\Program Files\eryph\guest-services\bin\egs-service.exe' collect-logs C:\Windows\Temp\egs-bundle.zip"
      ssh.exe -o StrictHostKeyChecking=no $hostName $bundleCmd 2>&1 | Out-Null
      egs-tool download-file $vmId 'C:\Windows\Temp\egs-bundle.zip' (Join-Path $OutputDir 'egs-bundle.zip') 2>&1 | Out-Null
    }
    catch {
      Write-Host "collect-logs bundle pull failed (non-fatal): $_"
    }
  }
  finally {
    $PSNativeCommandUseErrorActionPreference = $savedPref
  }
  Write-Host "Guest diagnostics saved to $OutputDir"
}

function Push-CatletExternalKvp {
  <#
  .SYNOPSIS
    Writes a single key/value into the External KVP pool of a (running)
    Hyper-V VM, bypassing egs-tool. Used by the multi-key e2e to push
    arbitrary authorized client keys into named slots without mutating the
    host's local egs-tool key files.

  .DESCRIPTION
    Mirrors what HostDataExchange.SetExternalValuesAsync does in C#: build
    a Msvm_KvpExchangeDataItem (Source = HostExternal = 0), serialize to a
    WMI-DTD embedded instance, and invoke AddKvpItems on the host's
    Msvm_VirtualSystemManagementService. Falls back to ModifyKvpItems when
    the slot already exists. Returns when the operation completes (poll-
    short-sleep for the async-job code path).

    Requires administrator + Hyper-V.
  #>
  [CmdletBinding()]
  param(
    [Parameter(Mandatory = $true)] [Guid] $VmId,
    [Parameter(Mandatory = $true)] [string] $Key,
    [Parameter(Mandatory = $true)] [string] $Value
  )

  $ns = 'root\virtualization\v2'
  $vm = Get-CimInstance -Namespace $ns -ClassName Msvm_ComputerSystem `
    -Filter "Name = '$VmId'"
  if (-not $vm) { throw "Hyper-V VM $VmId not found in $ns" }

  $vmms = Get-CimInstance -Namespace $ns -ClassName Msvm_VirtualSystemManagementService

  $itemClass = Get-CimClass -Namespace $ns -ClassName Msvm_KvpExchangeDataItem
  $item = New-CimInstance -CimClass $itemClass -Property @{
    Name = $Key
    Data = $Value
    Source = [UInt16]0   # HostExternal
  } -ClientOnly

  $serializer = [Microsoft.Management.Infrastructure.Serialization.CimSerializer]::Create()
  $bytes = $serializer.Serialize($item, `
    [Microsoft.Management.Infrastructure.Serialization.InstanceSerializationOptions]::None)
  $embedded = [System.Text.Encoding]::Unicode.GetString($bytes)

  # Try Add first; on any non-success/non-async return, fall through to Modify.
  $result = Invoke-CimMethod -InputObject $vmms -MethodName 'AddKvpItems' `
    -Arguments @{ TargetSystem = $vm; DataItems = @($embedded) }
  if ($result.ReturnValue -ne 0 -and $result.ReturnValue -ne 4096) {
    $result = Invoke-CimMethod -InputObject $vmms -MethodName 'ModifyKvpItems' `
      -Arguments @{ TargetSystem = $vm; DataItems = @($embedded) }
  }
  if ($result.ReturnValue -ne 0 -and $result.ReturnValue -ne 4096) {
    throw "Add/ModifyKvpItems for '$Key' failed: ReturnValue=$($result.ReturnValue)"
  }

  # 4096 == async job; sleep briefly. KVP writes are sub-second so this is
  # ample without us having to thread a Msvm_ConcreteJob poller.
  if ($result.ReturnValue -eq 4096) {
    Start-Sleep -Seconds 2
  }
}

function Test-CatletSshWithKey {
  <#
  .SYNOPSIS
    Returns the exit code of `ssh hostname` against a catlet using the
    supplied identity file. Zero = auth succeeded; non-zero = auth failed
    (or the proxy/network broke). Suppresses prompts so a failed auth is
    a fast, deterministic non-zero exit.
  #>
  [CmdletBinding()]
  param(
    [Parameter(Mandatory = $true)] [Guid] $VmId,
    [Parameter(Mandatory = $true)] [string] $IdentityFile,
    [Parameter()] [int] $TimeoutSeconds = 10
  )

  $savedPref = $PSNativeCommandUseErrorActionPreference
  $PSNativeCommandUseErrorActionPreference = $false
  try {
    $proxy = "egs-tool.exe proxy $VmId"
    # Capture stderr so a failure here yields an actionable log instead of
    # an opaque exit-255. Pester redirection / variable-scope differences
    # have bitten us before; the log file makes both rerunnable and easy
    # to diff against an interactive ssh probe.
    # `-F NUL` ignores ~/.ssh/config entirely. Without it, the VM's
    # Host block (written by egs-tool add-ssh-config) matches the
    # *.hyper-v.alt target and silently adds `IdentityFile
    # C:\ProgramData\eryph\guest-services\private\id_egs` to the list of
    # keys ssh.exe offers — even with IdentitiesOnly=yes, because that
    # flag merely restricts to "keys explicitly listed in config or -i",
    # and the Host-block IdentityFile counts as explicit. The fallback
    # makes a "this key should be REJECTED" test pass spuriously, because
    # the provisioned id_egs key still authorizes after the ephemeral
    # bounces off the gate. ProxyCommand is supplied via -o so we don't
    # need the Host block for routing either.
    $output = & $script:WinSshExe `
      -F NUL `
      -i $IdentityFile `
      -o "IdentitiesOnly=yes" `
      -o "StrictHostKeyChecking=no" `
      -o "UserKnownHostsFile=$env:TEMP\egs-remoteaccess-known_hosts.tmp" `
      -o "BatchMode=yes" `
      -o "PasswordAuthentication=no" `
      -o "KbdInteractiveAuthentication=no" `
      -o "ConnectTimeout=$TimeoutSeconds" `
      -o "ProxyCommand=$proxy" `
      -v `
      "egs@$($VmId).hyper-v.alt" hostname 2>&1
    $exit = $LASTEXITCODE
    if ($exit -ne 0) {
      $logDir = Join-Path $env:TEMP "egs-remoteaccess-e2e-ssh-logs"
      New-Item -ItemType Directory -Path $logDir -Force -ErrorAction SilentlyContinue | Out-Null
      $log = Join-Path $logDir "$VmId-$([DateTime]::UtcNow.Ticks).log"
      "exit=$exit`nargs: -i $IdentityFile / egs@$VmId.hyper-v.alt hostname / ProxyCommand=$proxy`n----`n" + ($output | Out-String) `
        | Set-Content -LiteralPath $log -Encoding utf8
      Write-Host "ssh probe failed (exit=$exit); log: $log"
    }
    return $exit
  }
  finally {
    $PSNativeCommandUseErrorActionPreference = $savedPref
    Remove-Item -LiteralPath "$env:TEMP\egs-remoteaccess-known_hosts.tmp" -Force -ErrorAction SilentlyContinue
  }
}

function Set-CatletServiceControlFlag {
  <#
  .SYNOPSIS
    Writes (or clears) a HKLM\SOFTWARE\eryph\guest-services\<Name> REG_DWORD
    inside a running catlet AND restarts eryph-guest-services so the new flag
    value is in force. The service caches its control flags at startup
    (matches IsRemoteAccessEnabled semantics), so a registry change alone is
    invisible to the running service until restart.

    Uses PowerShell Direct (Hyper-V VMBus) for BOTH the registry write and
    the service restart — no networking, no SSH, no firewall. The caller
    supplies a Credential for an admin user on the catlet. This channel is
    independent of eryph-guest-services lifecycle, so stopping it does NOT
    kill the admin session.
  #>
  [CmdletBinding()]
  param(
    [Parameter(Mandatory = $true)] $Catlet,
    [Parameter(Mandatory = $true)] [string] $Name,
    [Parameter()] [Nullable[int]] $Value,  # null = delete, otherwise REG_DWORD
    [Parameter(Mandatory = $true)] [pscredential] $Credential
  )

  # All registry I/O + the restart go through PowerShell Direct. Same
  # channel for write, verify, and restart — survives the service stop.
  Invoke-Command -VMId $Catlet.VmId -Credential $Credential -ScriptBlock {
    param([string] $FlagName, [Nullable[int]] $TargetValue)
    $key = 'HKLM:\SOFTWARE\eryph\guest-services'
    if ($null -eq $TargetValue) {
      if (Test-Path -LiteralPath $key) {
        Remove-ItemProperty -LiteralPath $key -Name $FlagName -ErrorAction SilentlyContinue
      }
    } else {
      if (-not (Test-Path -LiteralPath $key)) {
        New-Item -Path $key -Force | Out-Null
      }
      Set-ItemProperty -LiteralPath $key -Name $FlagName -Type DWord -Value $TargetValue
    }
    # Read back so the caller can fail fast if the write didn't land
    # where egs-service reads from.
    $reg = Get-ItemProperty -LiteralPath $key -Name $FlagName -ErrorAction SilentlyContinue
    if ($null -ne $reg) { $reg.$FlagName } else { $null }
  } -ArgumentList $Name, $Value -OutVariable readBack | Out-Null

  if ($null -eq $Value) {
    if ($null -ne $readBack[0]) {
      throw "$Name clear did not take effect on $($Catlet.Name); still reads $($readBack[0])."
    }
  } else {
    if ($readBack[0] -ne $Value) {
      throw "$Name=$Value not visible on $($Catlet.Name) (read back $($readBack[0]))."
    }
  }

  # Restart eryph-guest-services so the new (cached-at-startup) flag is
  # in force. PowerShell Direct is independent of the service, so this
  # works cleanly even though we're stopping the SSH listener that other
  # tests rely on.
  Invoke-Command -VMId $Catlet.VmId -Credential $Credential -ScriptBlock {
    Restart-Service -Name eryph-guest-services -Force
  } | Out-Null

  # Poll until the SSH listener is back via the egs channel.
  $deadline = (Get-Date).AddMinutes(2)
  $status = ''
  while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 2
    try { $status = & egs-tool get-status $Catlet.VmId 2>&1 } catch { $status = $_.Exception.Message }
    if ($status -eq 'available') { break }
  }
  if ($status -ne 'available') {
    throw "egs-service did not return to 'available' within 2 minutes after restart (status=$status)"
  }
}

function Set-CatletKvpAuthEnabled {
  <#
  .SYNOPSIS
    Convenience wrapper over Set-CatletServiceControlFlag for the
    KvpAuthEnabled flag (Windows).
  #>
  [CmdletBinding()]
  param(
    [Parameter(Mandatory = $true)] $Catlet,
    [Parameter()] [Nullable[int]] $Value,
    [Parameter(Mandatory = $true)] [pscredential] $Credential
  )
  Set-CatletServiceControlFlag -Catlet $Catlet -Name 'KvpAuthEnabled' `
    -Value $Value -Credential $Credential
}

function New-EphemeralSshKey {
  <#
  .SYNOPSIS
    Generates an ephemeral ecdsa-sha2-nistp256 keypair into a temp
    directory and returns a record with the private-key path and
    public-key OpenSSH string. The caller owns cleanup.

  .NOTES
    Algorithm is ecdsa-sha2-nistp256 because the egs-service's SSH
    library (Microsoft.DevTunnels.Ssh) advertises only rsa-sha2-* and
    ecdsa-sha2-nistp{256,384}. An ed25519 ephemeral key parses and is
    accepted into the authorized set on the guest, but the handshake
    fails with "PublicKeyAlgorithm not supported: ssh-ed25519" — the
    library can't VERIFY ed25519 signatures.
  #>
  [CmdletBinding()]
  param(
    [Parameter()] [string] $Comment = 'multikey-e2e-ephemeral'
  )

  $dir = Join-Path $env:TEMP "egs-multikey-$([guid]::NewGuid().ToString('N'))"
  New-Item -ItemType Directory -Path $dir -Force | Out-Null
  $key = Join-Path $dir 'id'

  # Pin to the Windows OpenSSH ssh-keygen so the produced key file is
  # readable by the Windows OpenSSH ssh.exe we use to authenticate. The
  # Git-Bash variant writes files OK but the ACL pattern can trip an
  # IdentityFile permissions check on Windows.
  #
  # -N '' creates a passphraseless key. Do NOT use -N '""' — PowerShell
  # passes those two double-quote characters through verbatim and
  # ssh-keygen encrypts the key with a literal two-char passphrase, which
  # ssh.exe in BatchMode then silently fails to decrypt (server accepts
  # the public key offer, client can't sign, ssh exits 255).
  $sshKeygen = Join-Path $env:WINDIR 'System32\OpenSSH\ssh-keygen.exe'
  & $sshKeygen -t ecdsa -b 256 -N '' -f $key -C $Comment | Out-Null
  if ($LASTEXITCODE -ne 0) {
    throw "ssh-keygen failed: $LASTEXITCODE"
  }
  return [pscustomobject]@{
    PrivateKeyPath = $key
    PublicKey = (Get-Content -Raw -LiteralPath "$key.pub").Trim()
    TempDir = $dir
  }
}

function Set-CatletChassisAssetTag {
  <#
  .SYNOPSIS
    Sets the SMBIOS chassis asset tag on a (stopped) catlet's Hyper-V VM.

  .DESCRIPTION
    eryph exposes no property for SMBIOS strings, and Hyper-V cannot set the
    system-product-name at all — but the chassis asset tag IS settable via
    Msvm_VirtualSystemSettingData.ChassisAssetTag + the management service's
    ModifySystemSettings. The egs OpenStack datasource's ds_detect accepts
    chassis-asset-tag == "OpenStack Nova" (a VALID_DMI_ASSET_TAGS value), so
    this is how an eryph/Hyper-V guest is made to trip the real detection gate.
    The guest reads it back as Win32_SystemEnclosure.SMBIOSAssetTag.

    Requires administrator + Hyper-V. The VM should be Off when applied.
  #>
  [CmdletBinding()]
  param(
    [Parameter(Mandatory = $true)] [Guid] $VmId,
    [Parameter(Mandatory = $true)] [string] $AssetTag
  )

  $ns = 'root\virtualization\v2'
  $vm = Get-CimInstance -Namespace $ns -ClassName Msvm_ComputerSystem -Filter "Name = '$VmId'"
  if (-not $vm) { throw "Hyper-V VM $VmId not found in $ns" }

  # The realized settings instance (snapshots also associate; pick Realized).
  $settings = Get-CimAssociatedInstance -InputObject $vm `
    -ResultClassName 'Msvm_VirtualSystemSettingData' `
    -Association 'Msvm_SettingsDefineState'
  $vssd = $settings |
    Where-Object { $_.VirtualSystemType -eq 'Microsoft:Hyper-V:System:Realized' } |
    Select-Object -First 1
  if (-not $vssd) { $vssd = $settings | Select-Object -First 1 }
  if (-not $vssd) { throw "No Msvm_VirtualSystemSettingData for VM $VmId" }

  $vssd.ChassisAssetTag = $AssetTag

  $mgmt = Get-CimInstance -Namespace $ns -ClassName Msvm_VirtualSystemManagementService
  $serializer = [Microsoft.Management.Infrastructure.Serialization.CimSerializer]::Create()
  $bytes = $serializer.Serialize($vssd, [Microsoft.Management.Infrastructure.Serialization.InstanceSerializationOptions]::None)
  $embedded = [System.Text.Encoding]::Unicode.GetString($bytes)

  $result = Invoke-CimMethod -InputObject $mgmt -MethodName 'ModifySystemSettings' `
    -Arguments @{ SystemSettings = $embedded }
  if ($result.ReturnValue -ne 0) {
    throw "ModifySystemSettings(ChassisAssetTag='$AssetTag') returned $($result.ReturnValue)"
  }
  Write-Verbose "Set ChassisAssetTag='$AssetTag' on VM $VmId"
}

function Set-OfflineProvisioningSettings {
  <#
  .SYNOPSIS
    Writes egs-provisioning.json into a stopped catlet's egs-service bin dir,
    via an offline VHD mount, so the agent reads it on first boot.

  .DESCRIPTION
    egs-service loads settings from egs-provisioning.json next to the binary
    (AppContext.BaseDirectory) — see ProvisioningSettings.CandidatePaths. The
    OpenStack e2e uses this to pin dataSources.dataSourceList to ["OpenStack"]
    so the locator probes only the metadata service and not eryph's own
    config-2 drive (ConfigDrive priority 40 would otherwise win over OpenStack
    priority 50).

  .PARAMETER Settings
    A hashtable serialized to JSON verbatim — must match the camelCase
    ProvisioningSettings shape, e.g.
      @{ dataSources = @{ dataSourceList = @('OpenStack') } }
  #>
  [CmdletBinding()]
  param(
    [Parameter(Mandatory = $true)] [Guid] $VmId,
    [Parameter(Mandatory = $true)] [hashtable] $Settings
  )

  $status = (Get-VM -Id $VmId).State
  if ($status -eq 'Running' -or $status -eq 'Saved') {
    throw "Catlet must be Stopped (current: $status) before mounting its VHD."
  }

  $mount = Mount-CatletVhd -VmId $VmId
  try {
    $binDir = Join-Path $mount.VolumeRoot 'Program Files\eryph\guest-services\bin'
    if (-not (Test-Path -LiteralPath $binDir)) {
      throw "egs-service bin dir missing at $binDir; cannot write egs-provisioning.json."
    }
    $json = $Settings | ConvertTo-Json -Depth 10
    $target = Join-Path $binDir 'egs-provisioning.json'
    Write-Verbose "Writing offline provisioning settings to $target`n$json"
    Set-Content -LiteralPath $target -Value $json -Encoding utf8
  }
  finally {
    Dismount-CatletVhd -VhdPath $mount.VhdPath
  }
}

# ---------------------------------------------------------------------------
# Linux catlet helpers — direct sshd as the admin channel
# ---------------------------------------------------------------------------
#
# PowerShell Direct does NOT work for Linux guests (Hyper-V VMBus PSDirect is
# Windows-guest only). On Linux the admin channel is plain OpenSSH on TCP/22
# as the OS user the linux-starter fodder gene provisioned with our public
# key — cloud-init enables sshd by default on Ubuntu, so the channel exists
# from first boot. Same architectural property as PsDirect on Windows: the
# channel is INDEPENDENT of eryph-guest-services, so we can stop/start that
# service without losing the session.

function Invoke-CatletAdminSshLinux {
  <#
  .SYNOPSIS
    Runs a shell command on a Linux catlet over its direct sshd, as the
    cloud-init-provisioned admin user, authenticated by the host's
    egs-tool private key (whose public half the linux-starter fodder
    placed in the user's authorized_keys).
  #>
  [CmdletBinding()]
  param(
    [Parameter(Mandatory = $true)] $Catlet,
    [Parameter(Mandatory = $true)] [string] $Username,
    [Parameter(Mandatory = $true)] [string] $Command,
    [Parameter()] [int] $ConnectTimeoutSeconds = 15
  )

  $catletIp = (Get-CatletIp -Id $Catlet.Id).IpAddress
  if (-not $catletIp) {
    throw "Get-CatletIp returned no IP for $($Catlet.Name); admin channel unreachable."
  }
  $idEgs = Join-Path $env:ProgramData 'eryph\guest-services\private\id_egs'
  # -F NUL to bypass any local ssh_config Host blocks (egs-tool
  # add-ssh-config writes *.hyper-v.alt patterns, but the
  # IP target shouldn't match — be defensive).
  return & $script:WinSshExe `
    -F NUL `
    -i $idEgs `
    -o IdentitiesOnly=yes `
    -o StrictHostKeyChecking=no `
    -o "UserKnownHostsFile=$env:TEMP\egs-linux-known_hosts.tmp" `
    -o BatchMode=yes `
    -o "ConnectTimeout=$ConnectTimeoutSeconds" `
    "$Username@$catletIp" $Command
}

function Wait-LinuxCatletSshReady {
  <#
  .SYNOPSIS
    Polls direct sshd on the catlet's IP as the admin user until a probe
    command (or `true`) returns 0. Times out with throw.
  #>
  [CmdletBinding()]
  param(
    [Parameter(Mandatory = $true)] $Catlet,
    [Parameter(Mandatory = $true)] [string] $Username,
    [Parameter()] [timespan] $Timeout = (New-TimeSpan -Minutes 10),
    [Parameter()] [timespan] $Interval = (New-TimeSpan -Seconds 5)
  )

  $savedPref = $PSNativeCommandUseErrorActionPreference
  $PSNativeCommandUseErrorActionPreference = $false
  try {
    $deadline = (Get-Date).Add($Timeout)
    while ((Get-Date) -lt $deadline) {
      Invoke-CatletAdminSshLinux -Catlet $Catlet -Username $Username -Command 'true' 2>&1 | Out-Null
      if ($LASTEXITCODE -eq 0) { return }
      Start-Sleep -Seconds $Interval.TotalSeconds
    }
    throw "Linux catlet $($Catlet.Name) sshd did not become reachable within $($Timeout.TotalSeconds)s."
  }
  finally {
    $PSNativeCommandUseErrorActionPreference = $savedPref
  }
}

function Update-EgsServiceLinuxOnline {
  <#
  .SYNOPSIS
    Replaces the egs-service binaries on a running Linux catlet with a
    locally-built linux-x64 publish, preserving /etc/opt/eryph/guest-services
    (host key + id_egs.pub live there). Restarts the systemd unit
    `eryph-guest-services` via direct sshd — never via the egs vsock
    channel.

  .NOTES
    Per project memory, a single-DLL hot-swap is unsafe (ABI mismatch
    between mixed builds → crash-loop). This helper REPLACES the entire
    bin directory contents.
  #>
  [CmdletBinding()]
  param(
    [Parameter(Mandatory = $true)] $Catlet,
    [Parameter(Mandatory = $true)] [string] $Username,
    [Parameter(Mandatory = $true)] [string] $PublishPath
  )

  if (-not (Test-Path -LiteralPath $PublishPath)) {
    throw "PublishPath not found: $PublishPath. Run 'dotnet publish -r linux-x64' first."
  }
  if (-not (Test-Path -LiteralPath (Join-Path $PublishPath 'egs-service'))) {
    throw "egs-service binary not present under $PublishPath; check publish target (linux-x64)."
  }

  $catletIp = (Get-CatletIp -Id $Catlet.Id).IpAddress
  if (-not $catletIp) { throw "No IP for $($Catlet.Name)" }
  $idEgs = Join-Path $env:ProgramData 'eryph\guest-services\private\id_egs'

  # Staging dir in the user's home — scp can write there without sudo;
  # the install step then moves the files with sudo.
  $stamp = [guid]::NewGuid().ToString('N').Substring(0, 8)
  $remoteDir = "/tmp/egs-publish-$stamp"

  $savedPref = $PSNativeCommandUseErrorActionPreference
  $PSNativeCommandUseErrorActionPreference = $false
  try {
    Invoke-CatletAdminSshLinux -Catlet $Catlet -Username $Username `
      -Command "mkdir -p $remoteDir" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Failed to create staging dir on $($Catlet.Name)." }

    Write-Verbose "Uploading $PublishPath -> $($Catlet.Name):$remoteDir"
    # scp -r copies directory contents recursively. Bash's brace
    # expansion isn't portable here — let scp do the recursion.
    & $script:WinScpExe `
      -i $idEgs `
      -o IdentitiesOnly=yes `
      -o StrictHostKeyChecking=no `
      -o "UserKnownHostsFile=$env:TEMP\egs-linux-known_hosts.tmp" `
      -o BatchMode=yes `
      -r `
      "$PublishPath/*" `
      "${Username}@${catletIp}:$remoteDir/" 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "scp upload to $($Catlet.Name) returned $LASTEXITCODE." }

    # All-in-one install: stop service, wipe bin, copy, set executable bit,
    # restart. Preserve /etc/opt/eryph/guest-services entirely. Single line
    # with `&&` so it survives the ssh.exe -> remote-shell hop without
    # newline-handling surprises.
    $installCmd = 'set -e && ' +
      'sudo systemctl stop eryph-guest-services && ' +
      'sudo find /opt/eryph/guest-services/bin -mindepth 1 -delete && ' +
      "sudo cp -r $remoteDir/. /opt/eryph/guest-services/bin/ && " +
      'sudo chmod +x /opt/eryph/guest-services/bin/egs-service && ' +
      'sudo systemctl start eryph-guest-services && ' +
      "rm -rf $remoteDir"
    $installOutput = Invoke-CatletAdminSshLinux -Catlet $Catlet -Username $Username `
      -Command $installCmd 2>&1
    if ($LASTEXITCODE -ne 0) {
      $logDir = Join-Path $env:TEMP 'egs-remoteaccess-linux-e2e-logs'
      New-Item -ItemType Directory -Path $logDir -Force -ErrorAction SilentlyContinue | Out-Null
      $log = Join-Path $logDir "$($Catlet.VmId)-install-$([DateTime]::UtcNow.Ticks).log"
      "exit=$LASTEXITCODE`ncmd:`n$installCmd`n----`n" + ($installOutput | Out-String) `
        | Set-Content -LiteralPath $log -Encoding utf8
      throw "Binary install on $($Catlet.Name) returned $LASTEXITCODE; log: $log"
    }
  }
  finally {
    $PSNativeCommandUseErrorActionPreference = $savedPref
  }
}

function Write-LinuxEgsClientKey {
  <#
  .SYNOPSIS
    Writes a public key to /etc/opt/eryph/guest-services/id_egs.pub on a
    running Linux catlet via the direct sshd admin channel. egs-service
    reads this file on startup as the provisioned authorized identity.
  #>
  [CmdletBinding()]
  param(
    [Parameter(Mandatory = $true)] $Catlet,
    [Parameter(Mandatory = $true)] [string] $Username,
    [Parameter(Mandatory = $true)] [string] $PublicKey
  )

  # base64 the content to avoid quoting/escaping pitfalls through
  # ssh.exe -> shell. The remote `tee` writes with root privileges via
  # sudo; the parent dir is created by the egs-service package install.
  $content = $PublicKey.TrimEnd() + "`n"
  $b64 = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($content))
  $cmd = "echo $b64 | base64 -d | sudo tee /etc/opt/eryph/guest-services/id_egs.pub > /dev/null"
  Invoke-CatletAdminSshLinux -Catlet $Catlet -Username $Username `
    -Command $cmd 2>&1 | Out-Null
  if ($LASTEXITCODE -ne 0) {
    throw "Writing id_egs.pub on $($Catlet.Name) returned $LASTEXITCODE."
  }
}

function Set-CatletServiceControlFlagLinux {
  <#
  .SYNOPSIS
    Writes / clears a single control flag in
    /etc/opt/eryph/guest-services/service-control.conf on a running
    Linux catlet, then restarts eryph-guest-services so the new value
    is in force (PlatformServiceControlFlags caches at startup).

    The conf is written with just the one KEY=VALUE line (clearing
    removes the file entirely → all flags back to default). That single-
    flag-per-file shape is fine for the isolated e2e contexts here.

    Channel: direct sshd as the admin user — independent of
    eryph-guest-services lifecycle.
  #>
  [CmdletBinding()]
  param(
    [Parameter(Mandatory = $true)] $Catlet,
    [Parameter(Mandatory = $true)] [string] $Username,
    [Parameter(Mandatory = $true)] [string] $Name,
    [Parameter()] [Nullable[int]] $Value
  )

  $confPath = '/etc/opt/eryph/guest-services/service-control.conf'

  if ($null -eq $Value) {
    # Clear: just remove the file (no flags = all defaults). Tolerate
    # "file already absent" — rm -f is idempotent.
    $writeCmd = "sudo rm -f $confPath"
  } else {
    $content = "$Name=$Value`n"
    $b64 = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($content))
    $writeCmd = "echo $b64 | base64 -d | sudo tee $confPath > /dev/null"
  }

  $savedPref = $PSNativeCommandUseErrorActionPreference
  $PSNativeCommandUseErrorActionPreference = $false
  try {
    Invoke-CatletAdminSshLinux -Catlet $Catlet -Username $Username `
      -Command $writeCmd 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) {
      throw "service-control.conf write on $($Catlet.Name) returned $LASTEXITCODE."
    }

    # Read back so a path mismatch surfaces immediately.
    $verifyCmd = "test -f $confPath && cat $confPath || echo MISSING"
    $readBack = (Invoke-CatletAdminSshLinux -Catlet $Catlet -Username $Username `
      -Command $verifyCmd) | Out-String
    if ($null -eq $Value) {
      if ($readBack -notmatch 'MISSING') {
        throw "$Name clear did not take effect on $($Catlet.Name); conf still:`n$readBack"
      }
    } else {
      if ($readBack -notmatch "$Name\s*=\s*$Value") {
        throw "$Name=$Value not visible in conf on $($Catlet.Name). Got:`n$readBack"
      }
    }

    # Restart over the admin channel (NOT via egs vsock — that would die
    # with the service). systemctl restart blocks until the unit is
    # active or failed; the egs-status poll below catches a failed unit.
    Invoke-CatletAdminSshLinux -Catlet $Catlet -Username $Username `
      -Command 'sudo systemctl restart eryph-guest-services' 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) {
      throw "systemctl restart on $($Catlet.Name) returned $LASTEXITCODE."
    }

    # Poll egs-status via host-side KVP until the new instance reports
    # 'available' again.
    $deadline = (Get-Date).AddMinutes(2)
    $status = ''
    while ((Get-Date) -lt $deadline) {
      Start-Sleep -Seconds 2
      try { $status = & egs-tool get-status $Catlet.VmId 2>&1 } catch { $status = $_.Exception.Message }
      if ($status -eq 'available') { break }
    }
    if ($status -ne 'available') {
      throw "egs-service did not return to 'available' within 2 minutes after restart on $($Catlet.Name) (status=$status)"
    }
  }
  finally {
    $PSNativeCommandUseErrorActionPreference = $savedPref
  }
}

function Set-CatletKvpAuthEnabledLinux {
  <#
  .SYNOPSIS
    Convenience wrapper over Set-CatletServiceControlFlagLinux for the
    KvpAuthEnabled flag.
  #>
  [CmdletBinding()]
  param(
    [Parameter(Mandatory = $true)] $Catlet,
    [Parameter(Mandatory = $true)] [string] $Username,
    [Parameter()] [Nullable[int]] $Value
  )
  Set-CatletServiceControlFlagLinux -Catlet $Catlet -Username $Username `
    -Name 'KvpAuthEnabled' -Value $Value
}

# ---------------------------------------------------------------------------
# Port-forwarding e2e helpers (shared host side + per-OS guest listeners)
# ---------------------------------------------------------------------------
#
# The port-forwarding gate is proven by a real `ssh -L` tunnel through the egs
# proxy: a tiny TCP listener inside the guest answers a fixed token on
# 127.0.0.1:<guestPort>, and the host opens
#   ssh -L 127.0.0.1:<localPort>:127.0.0.1:<guestPort>
# then reads the token back through the tunnel. This exercises the same
# `direct-tcpip` path a jump host uses (the guest dials the target); a loopback
# target keeps the test self-contained while proving the channel is honored.
#
# When PortForwardingEnabled is off the egs SSH server never registers the
# DevTunnels PortForwardingService, so it refuses the direct-tcpip channel: the
# client's local listener still accepts the TCP connect, but no data flows and
# the token read comes back empty.

function Get-FreeLocalTcpPort {
  <#
  .SYNOPSIS
    Returns a currently-free TCP port on the host loopback (bind-to-0 trick).
    Small TOCTOU window, acceptable for a single test process.
  #>
  $l = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
  $l.Start()
  try { return [int]($l.LocalEndpoint).Port }
  finally { $l.Stop() }
}

function Start-CatletLoopbackTcpProbe {
  <#
  .SYNOPSIS
    Starts a loopback TCP listener inside a WINDOWS catlet (via PowerShell
    Direct) that writes a fixed token to every client and closes. Runs as a
    detached SYSTEM scheduled task so it survives the Invoke-Command session
    and the eryph-guest-services restarts the gate test triggers.
  #>
  [CmdletBinding()]
  param(
    [Parameter(Mandatory = $true)] $Catlet,
    [Parameter(Mandatory = $true)] [int] $Port,
    [Parameter(Mandatory = $true)] [string] $Token,
    [Parameter(Mandatory = $true)] [pscredential] $Credential,
    [Parameter()] [string] $TaskName = 'EgsFwdProbe'
  )

  # Built host-side ("@...@" is expandable: $Port/$Token interpolate here;
  # backtick-escaped $vars stay literal for the guest-side runtime).
  $listenerScript = @"
`$ErrorActionPreference = 'Stop'
`$listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $Port)
`$listener.Start()
while (`$true) {
  `$client = `$listener.AcceptTcpClient()
  try {
    `$stream = `$client.GetStream()
    `$bytes = [System.Text.Encoding]::ASCII.GetBytes('$Token')
    `$stream.Write(`$bytes, 0, `$bytes.Length)
    `$stream.Flush()
    Start-Sleep -Milliseconds 150
  } finally { `$client.Close() }
}
"@

  Invoke-Command -VMId $Catlet.VmId -Credential $Credential -ScriptBlock {
    param($scriptText, $tn)
    $path = 'C:\Windows\Temp\egs-fwdprobe.ps1'
    Set-Content -LiteralPath $path -Value $scriptText -Encoding utf8
    schtasks.exe /create /tn $tn `
      /tr "powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File $path" `
      /sc once /st 23:59:59 /ru SYSTEM /rl HIGHEST /f | Out-Null
    schtasks.exe /run /tn $tn | Out-Null
  } -ArgumentList $listenerScript, $TaskName | Out-Null

  # Give the task a moment to bind before the first probe.
  Start-Sleep -Seconds 2
}

function Stop-CatletLoopbackTcpProbe {
  [CmdletBinding()]
  param(
    [Parameter(Mandatory = $true)] $Catlet,
    [Parameter(Mandatory = $true)] [pscredential] $Credential,
    [Parameter()] [string] $TaskName = 'EgsFwdProbe'
  )
  Invoke-Command -VMId $Catlet.VmId -Credential $Credential -ScriptBlock {
    param($tn)
    schtasks.exe /end /tn $tn 2>$null | Out-Null
    schtasks.exe /delete /tn $tn /f 2>$null | Out-Null
    Get-CimInstance Win32_Process -Filter "Name = 'powershell.exe'" -ErrorAction SilentlyContinue |
      Where-Object { $_.CommandLine -like '*egs-fwdprobe.ps1*' } |
      ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
  } -ArgumentList $TaskName | Out-Null
}

function Start-CatletLoopbackTcpProbeLinux {
  <#
  .SYNOPSIS
    Starts a loopback TCP listener inside a LINUX catlet (via direct sshd)
    that writes a fixed token to every client and closes. Detached with
    setsid+nohup so it survives the admin ssh session and service restarts.
    Uses python3 (present on the Ubuntu starter).
  #>
  [CmdletBinding()]
  param(
    [Parameter(Mandatory = $true)] $Catlet,
    [Parameter(Mandatory = $true)] [string] $Username,
    [Parameter(Mandatory = $true)] [int] $Port,
    [Parameter(Mandatory = $true)] [string] $Token
  )

  $py = @"
import socket
s = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
s.bind(('127.0.0.1', $Port))
s.listen(5)
while True:
    c, _ = s.accept()
    try:
        c.sendall(b'$Token')
    finally:
        c.close()
"@
  $b64 = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($py))
  $cmd = "echo $b64 | base64 -d > /tmp/egs-fwdprobe.py && " +
         "setsid nohup python3 /tmp/egs-fwdprobe.py >/tmp/egs-fwdprobe.log 2>&1 & echo started"
  $savedPref = $PSNativeCommandUseErrorActionPreference
  $PSNativeCommandUseErrorActionPreference = $false
  try {
    Invoke-CatletAdminSshLinux -Catlet $Catlet -Username $Username -Command $cmd 2>&1 | Out-Null
    Start-Sleep -Seconds 2
  }
  finally {
    $PSNativeCommandUseErrorActionPreference = $savedPref
  }
}

function Stop-CatletLoopbackTcpProbeLinux {
  [CmdletBinding()]
  param(
    [Parameter(Mandatory = $true)] $Catlet,
    [Parameter(Mandatory = $true)] [string] $Username
  )
  $savedPref = $PSNativeCommandUseErrorActionPreference
  $PSNativeCommandUseErrorActionPreference = $false
  try {
    Invoke-CatletAdminSshLinux -Catlet $Catlet -Username $Username `
      -Command 'pkill -f egs-fwdprobe.py; rm -f /tmp/egs-fwdprobe.py' 2>&1 | Out-Null
  }
  finally {
    $PSNativeCommandUseErrorActionPreference = $savedPref
  }
}

function Test-CatletPortForward {
  <#
  .SYNOPSIS
    Opens `ssh -L 127.0.0.1:<local>:127.0.0.1:<GuestPort>` through the egs
    proxy with the supplied identity, reads from the local end, and returns
    $true iff the guest-side token came back over the tunnel. $false means
    the forward was refused (server didn't register PortForwardingService) or
    no data flowed before timeout.

    The actual data round-trip — not just "did ssh connect" — is the signal:
    with forwarding off the client's local listener still accepts the connect,
    so only reading the token distinguishes enabled from disabled.
  #>
  [CmdletBinding()]
  param(
    [Parameter(Mandatory = $true)] [Guid] $VmId,
    [Parameter(Mandatory = $true)] [string] $IdentityFile,
    [Parameter(Mandatory = $true)] [int] $GuestPort,
    [Parameter(Mandatory = $true)] [string] $ExpectedToken,
    [Parameter()] [int] $TimeoutSeconds = 12,
    # Remote command that just holds the session open while we drive the
    # forward. We deliberately do NOT use `ssh -N`: a channel-less session over
    # the egs proxy is torn down immediately (the local -L listener never even
    # comes up), so we keep an exec channel alive — the proven path — and run
    # the forward alongside it. Default is the Windows guest shell; the Linux
    # suite passes 'sleep 30'.
    [Parameter()] [string] $KeepAliveCommand = 'powershell -NoProfile -Command "Start-Sleep -Seconds 30"'
  )

  $localPort = Get-FreeLocalTcpPort
  $proxy = "egs-tool.exe proxy $VmId"
  $knownHosts = "$env:TEMP\egs-portfwd-known_hosts-$([guid]::NewGuid().ToString('N')).tmp"

  $sshArgs = @(
    '-F', 'NUL',
    '-i', $IdentityFile,
    '-o', 'IdentitiesOnly=yes',
    '-o', 'StrictHostKeyChecking=no',
    '-o', "UserKnownHostsFile=$knownHosts",
    '-o', 'BatchMode=yes',
    '-o', "ConnectTimeout=$TimeoutSeconds",
    '-o', "ProxyCommand=$proxy",
    '-L', "127.0.0.1:${localPort}:127.0.0.1:${GuestPort}",
    "egs@$($VmId).hyper-v.alt",
    $KeepAliveCommand
  )

  # Launch ssh in a background JOB, not Start-Process: a Start-Process child has
  # no console, and the egs-tool ProxyCommand it spawns then dies immediately
  # ("Connection closed by UNKNOWN port 65535"), so the -L listener never comes
  # up. A job runs in a child pwsh with proper stdio, exactly like the working
  # interactive `& ssh` path.
  $job = Start-Job -ScriptBlock {
    param($sshExe, $sshArgs)
    & $sshExe @sshArgs 2>&1
  } -ArgumentList $script:WinSshExe, $sshArgs

  try {
    # Wait for ssh's local listener to come up (it accepts regardless of the
    # server's forwarding policy — this is just readiness, not the verdict).
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $ready = $false
    while ((Get-Date) -lt $deadline) {
      if ($job.State -ne 'Running') { break }
      try {
        $t = [System.Net.Sockets.TcpClient]::new()
        $t.Connect('127.0.0.1', $localPort)
        $t.Close()
        $ready = $true
        break
      } catch { Start-Sleep -Milliseconds 300 }
    }
    if (-not $ready) {
      Write-Host "port-forward probe: local listener never came up (job state=$($job.State)). ssh output:"
      Receive-Job $job -ErrorAction SilentlyContinue | Select-Object -Last 5 | ForEach-Object { Write-Host "  ssh: $_" }
      return $false
    }

    # The real test: read the token through the tunnel. With forwarding off the
    # local listener still accepts, so only the data round-trip is the verdict.
    $received = ''
    $client = [System.Net.Sockets.TcpClient]::new()
    try {
      $iar = $client.BeginConnect('127.0.0.1', $localPort, $null, $null)
      if ($iar.AsyncWaitHandle.WaitOne([TimeSpan]::FromSeconds($TimeoutSeconds))) {
        $client.EndConnect($iar)
        $client.ReceiveTimeout = $TimeoutSeconds * 1000
        $stream = $client.GetStream()
        $buf = [byte[]]::new(256)
        try {
          $n = $stream.Read($buf, 0, $buf.Length)
          if ($n -gt 0) { $received = [System.Text.Encoding]::ASCII.GetString($buf, 0, $n) }
        } catch { }
      }
    } catch { }
    finally { $client.Close() }

    return ($received.Trim() -eq $ExpectedToken)
  }
  finally {
    Stop-Job $job -ErrorAction SilentlyContinue
    Remove-Job $job -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $knownHosts -Force -ErrorAction SilentlyContinue
  }
}
