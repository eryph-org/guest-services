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
