#Requires -Version 7.4

# Adapted from MaxBack/test/e2e/Helpers.ps1.

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

  egs-tool update-ssh-config

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
        -ArgumentList "$($catlet.Id).eryph.alt hostname" `
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
  $hostName = "$($Catlet.Id).eryph.alt"
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
    The catlet must NOT be running (otherwise Hyper-V holds the disk lock).
    This function intentionally returns the OS-partition drive letter rather
    than the disk number so callers can use straight Win32 paths to mutate
    the offline guest.

    Requires administrator (Mount-VHD needs HyperV management rights).
  #>
  [CmdletBinding()]
  param(
    [Parameter(Mandatory = $true)] [Guid] $VmId
  )

  $vhdPath = Get-CatletVhdPath -VmId $VmId
  Write-Verbose "Mounting catlet VHD: $vhdPath"
  $disk = Mount-VHD -Path $vhdPath -PassThru -ErrorAction Stop
  # Mount-VHD doesn't always populate the partition info synchronously; refresh.
  $disk = Get-Disk -Number $disk.Number

  $partitions = Get-Partition -DiskNumber $disk.Number
  foreach ($p in $partitions) {
    if (-not $p.DriveLetter) { continue }
    if (Test-Path -LiteralPath "$($p.DriveLetter):\Windows") {
      Write-Verbose "Found OS partition at $($p.DriveLetter):"
      return [pscustomobject]@{
        VhdPath     = $vhdPath
        DriveLetter = "$($p.DriveLetter)"
      }
    }
  }

  Dismount-VHD -Path $vhdPath -ErrorAction SilentlyContinue
  throw "Could not find a Windows OS partition on $vhdPath"
}

function Dismount-CatletVhd {
  [CmdletBinding()]
  param(
    [Parameter(Mandatory = $true)] [string] $VhdPath
  )
  Write-Verbose "Dismounting $VhdPath"
  Dismount-VHD -Path $VhdPath -ErrorAction Stop
}

function Install-EgsServiceOffline {
  <#
  .SYNOPSIS
    Bakes the locally-built egs-service into a stopped catlet's VHD, disables
    cloudbase-init, and registers egs-service as a Windows service via the
    offline SYSTEM registry hive so it autostarts at first boot.

  .PARAMETER VmId
    The catlet's VmId. The catlet must be stopped.

  .PARAMETER PublishPath
    Directory containing the `dotnet publish` output of egs-service (must
    include egs-service.exe and its dependencies).

  .NOTES
    Requires administrator. Operates ONLY on the offline VHD — does not
    touch the host's running services or registry.
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

  $catlet = Get-Catlet -Id $VmId -ErrorAction Stop
  $status = (Get-VM -Id $VmId).State
  if ($status -eq 'Running' -or $status -eq 'Saved') {
    throw "Catlet must be Stopped (current: $status) before mounting its VHD."
  }

  $mount = Mount-CatletVhd -VmId $VmId
  try {
    $root = "$($mount.DriveLetter):"
    $binDir = Join-Path $root 'Program Files\eryph\guest-services\bin'
    $cbiDir = Join-Path $root 'Program Files\Cloudbase Solutions\Cloudbase-Init'

    Write-Verbose "Copying egs-service binaries to $binDir"
    if (-not (Test-Path -LiteralPath $binDir)) {
      New-Item -ItemType Directory -Path $binDir -Force | Out-Null
    }
    Get-ChildItem -LiteralPath $PublishPath -File | ForEach-Object {
      Copy-Item -LiteralPath $_.FullName -Destination $binDir -Force
    }

    if (Test-Path -LiteralPath $cbiDir) {
      $disabledName = "$cbiDir.disabled-$(Get-Date -Format 'yyyyMMddHHmmss')"
      Write-Verbose "Disabling cloudbase-init: $cbiDir -> $disabledName"
      Move-Item -LiteralPath $cbiDir -Destination $disabledName -Force

      # Also disable any cbi service registration in the offline SYSTEM hive
      # so the SCM doesn't try to start a service whose binary just moved.
      Set-OfflineServiceStartType -DriveLetter $mount.DriveLetter `
        -ServiceName 'cloudbase-init' -StartType 4   # 4 = Disabled
    } else {
      Write-Verbose "cloudbase-init not present at $cbiDir; skipping disable step"
    }

    Register-OfflineService `
      -DriveLetter $mount.DriveLetter `
      -ServiceName 'eryph-guest-services' `
      -DisplayName 'eryph guest services' `
      -ImagePath  '"C:\Program Files\eryph\guest-services\bin\egs-service.exe"' `
      -Description 'Eryph guest channel + provisioning agent.'
  }
  finally {
    Dismount-CatletVhd -VhdPath $mount.VhdPath
  }
}

function Register-OfflineService {
  <#
  .SYNOPSIS
    Writes a Win32 service registration into the offline SYSTEM hive of a
    mounted Windows image so the SCM auto-starts it on first boot.

  .DESCRIPTION
    Loads <Drive>:\Windows\System32\config\SYSTEM under HKLM\OfflineSystem,
    creates Services\<ServiceName> with Start=2 (Automatic), Type=10 (Own
    Process), and the required value names; unloads the hive when done.
    Idempotent: existing registrations are overwritten.
  #>
  [CmdletBinding()]
  param(
    [Parameter(Mandatory = $true)] [string] $DriveLetter,
    [Parameter(Mandatory = $true)] [string] $ServiceName,
    [Parameter(Mandatory = $true)] [string] $DisplayName,
    [Parameter(Mandatory = $true)] [string] $ImagePath,
    [Parameter()] [string] $Description = ''
  )

  $hivePath = "$DriveLetter`:\Windows\System32\config\SYSTEM"
  if (-not (Test-Path -LiteralPath $hivePath)) {
    throw "Offline SYSTEM hive not found: $hivePath"
  }

  $tempHive = "HKLM\OfflineSystem_$([guid]::NewGuid().ToString('N'))"
  $regExe = "$env:WINDIR\System32\reg.exe"

  & $regExe load $tempHive $hivePath | Out-Null
  if ($LASTEXITCODE -ne 0) { throw "reg load failed for $hivePath" }

  try {
    # ControlSet001 is what becomes CurrentControlSet at boot.
    $svcKey = "$tempHive\ControlSet001\Services\$ServiceName"

    & $regExe add $svcKey /f /v ImagePath   /t REG_EXPAND_SZ /d $ImagePath   | Out-Null
    & $regExe add $svcKey /f /v DisplayName /t REG_SZ        /d $DisplayName | Out-Null
    if ($Description) {
      & $regExe add $svcKey /f /v Description /t REG_SZ /d $Description | Out-Null
    }
    & $regExe add $svcKey /f /v Start        /t REG_DWORD /d 2  | Out-Null  # 2 = Automatic
    & $regExe add $svcKey /f /v Type         /t REG_DWORD /d 16 | Out-Null  # 0x10 = Own Process
    & $regExe add $svcKey /f /v ErrorControl /t REG_DWORD /d 1  | Out-Null  # 1 = Normal
    & $regExe add $svcKey /f /v ObjectName   /t REG_SZ    /d 'LocalSystem' | Out-Null
  }
  finally {
    # Force garbage collect — reg.exe sometimes leaves the hive briefly busy.
    [System.GC]::Collect()
    [System.GC]::WaitForPendingFinalizers()
    & $regExe unload $tempHive | Out-Null
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
    [Parameter(Mandatory = $true)] [string] $DriveLetter,
    [Parameter(Mandatory = $true)] [string] $ServiceName,
    [Parameter(Mandatory = $true)] [int] $StartType
  )

  $hivePath = "$DriveLetter`:\Windows\System32\config\SYSTEM"
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
        $err = $kvp.guest.'eryph.provisioning.error'
        throw "Provisioning reported failed: $err"
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
