#Requires -Version 7.4

# Adapted from MaxBack/test/e2e/Helpers.ps1.

function New-TestProject {
  $projectName = "egs-shell-e2e-$(Get-Date -Format 'yyyyMMddHHmmss')"
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
  egs-tool upload-directory $Catlet.VmId $PublishPath 'C:\egs-staging' --overwrite

  # Deploy script: stop service, copy files, start. Runs detached via scheduled
  # task so the SSH session that triggers it can disconnect cleanly when the
  # service stops (the service IS our SSH server).
  $deployScript = @'
$ErrorActionPreference = 'Stop'
$logPath = 'C:\egs-staging\deploy.log'
try {
  "[$(Get-Date -Format o)] stopping service" | Out-File -Append $logPath
  Stop-Service -Name eryph-guest-services -Force
  $sw = [Diagnostics.Stopwatch]::StartNew()
  while ((Get-Service eryph-guest-services).Status -ne 'Stopped') {
    if ($sw.Elapsed.TotalSeconds -gt 60) { throw 'service did not stop in 60s' }
    Start-Sleep -Milliseconds 500
  }
  "[$(Get-Date -Format o)] copying files" | Out-File -Append $logPath
  Get-ChildItem 'C:\egs-staging' -File `
    | Where-Object { $_.Name -ne 'deploy.ps1' -and $_.Name -ne 'deploy.log' } `
    | Copy-Item -Destination 'C:\Program Files\eryph\guest-services\bin' -Force
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
  Wait-Assert -Timeout (New-TimeSpan -Minutes 3) {
    (egs-tool get-status $Catlet.VmId) | Should -Be 'available'
  }
  # Confirm the patched binary is the one running by checking version metadata.
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
    snippets into stdin. Returns the captured output (stdout+stderr merged).

  .DESCRIPTION
    Uses `ssh.exe -tt` to force PTY allocation even with redirected stdio, so
    the server-side ShellService is exercised (not CommandService).
  #>
  [CmdletBinding()]
  param(
    [Parameter(Mandatory = $true)] [string] $HostName,
    [Parameter(Mandatory = $true)] [string[]] $InputLines,
    [Parameter()] [hashtable] $SetEnv,
    [Parameter()] [int] $TimeoutSeconds = 60
  )

  $sshArgs = @('-tt', '-o', 'StrictHostKeyChecking=no')
  if ($SetEnv) {
    foreach ($k in $SetEnv.Keys) {
      $sshArgs += @('-o', "SetEnv=$k=$($SetEnv[$k])")
    }
  }
  $sshArgs += $HostName

  $stdinFile = [System.IO.Path]::Combine([System.IO.Path]::GetTempPath(), "egs-ssh-in-$([guid]::NewGuid()).txt")
  $stdoutFile = [System.IO.Path]::Combine([System.IO.Path]::GetTempPath(), "egs-ssh-out-$([guid]::NewGuid()).txt")
  Set-Content -Path $stdinFile -Value (($InputLines + 'exit') -join "`n") -NoNewline -Encoding utf8

  try {
    $process = Start-Process -FilePath 'ssh.exe' `
      -ArgumentList $sshArgs `
      -RedirectStandardInput $stdinFile `
      -RedirectStandardOutput $stdoutFile `
      -WindowStyle Hidden `
      -PassThru
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
      $process | Stop-Process -Force
      throw "ssh interactive session timed out after ${TimeoutSeconds}s"
    }
    return Get-Content -Raw -Path $stdoutFile
  } finally {
    Remove-Item -LiteralPath $stdinFile -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $stdoutFile -Force -ErrorAction SilentlyContinue
  }
}
