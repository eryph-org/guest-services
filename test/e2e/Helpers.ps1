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

function Invoke-InteractiveShell {
  <#
  .SYNOPSIS
    Opens an interactive SSH shell session and feeds the supplied PowerShell
    snippets into stdin. Returns the captured output (stdout + stderr).

  .DESCRIPTION
    Uses `ssh.exe -tt` to force PTY allocation even with redirected stdio, so
    the server-side ShellService is exercised (not CommandService). On
    failure or empty output, the captured stdout+stderr are written to host
    so the test diagnostics show what happened.
  #>
  [CmdletBinding()]
  param(
    [Parameter(Mandatory = $true)] [string] $HostName,
    [Parameter(Mandatory = $true)] [string[]] $InputLines,
    [Parameter()] [hashtable] $SetEnv,
    [Parameter()] [int] $TimeoutSeconds = 60
  )

  $psi = [System.Diagnostics.ProcessStartInfo]::new()
  $psi.FileName = 'ssh.exe'
  $psi.UseShellExecute = $false
  $psi.RedirectStandardInput = $true
  $psi.RedirectStandardOutput = $true
  $psi.RedirectStandardError = $true
  $psi.CreateNoWindow = $true

  $sshArgs = @('-tt', '-o', 'StrictHostKeyChecking=no')
  if ($SetEnv) {
    foreach ($k in $SetEnv.Keys) {
      $sshArgs += @('-o', "SetEnv=$k=$($SetEnv[$k])")
    }
  }
  $sshArgs += $HostName

  foreach ($a in $sshArgs) { $psi.ArgumentList.Add($a) }

  $proc = [System.Diagnostics.Process]::Start($psi)

  # Buffer the stdin payload, then close so the shell sees EOF and exits.
  # A short pause before 'exit' lets PowerShell flush its prompt + banner +
  # the marker output before the channel closes.
  $payload = (($InputLines + @('Start-Sleep -Milliseconds 500', 'exit')) -join "`r`n") + "`r`n"
  $proc.StandardInput.Write($payload)
  $proc.StandardInput.Flush()
  $proc.StandardInput.Close()

  $stdoutTask = $proc.StandardOutput.ReadToEndAsync()
  $stderrTask = $proc.StandardError.ReadToEndAsync()

  if (-not $proc.WaitForExit($TimeoutSeconds * 1000)) {
    $proc.Kill($true)
    Write-Host "ssh -tt timed out after ${TimeoutSeconds}s. Captured so far:"
    Write-Host "STDOUT: $($stdoutTask.Result)"
    Write-Host "STDERR: $($stderrTask.Result)"
    throw "ssh interactive session timed out"
  }

  $stdout = $stdoutTask.Result
  $stderr = $stderrTask.Result
  $merged = "$stdout$stderr"

  if ([string]::IsNullOrWhiteSpace($merged)) {
    Write-Host "ssh -tt produced no output. exit=$($proc.ExitCode)"
    Write-Host "STDOUT bytes: $([Text.Encoding]::UTF8.GetByteCount($stdout))"
    Write-Host "STDERR bytes: $([Text.Encoding]::UTF8.GetByteCount($stderr))"
  }

  return $merged
}
