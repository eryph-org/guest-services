#Requires -Version 7.4
#Requires -Module Pester
#Requires -Module Eryph.ComputeClient
param (
    [Parameter()]
    [ValidateSet('winsrv2019', 'winsrv2022', 'winsrv2025')]
    [string] $OSVersion = 'winsrv2022',

    [Parameter()]
    [string] $PublishPath,

    [Parameter()]
    [string] $ProbePath
)

BeforeAll {
  $PSNativeCommandUseErrorActionPreference = $true
  $ErrorActionPreference = 'Stop'
  . $PSScriptRoot/Helpers.ps1

  $catletConfigTemplate = Get-Content -Raw -Path $PSScriptRoot/catlet.yaml
  $catletConfig = $catletConfigTemplate `
    -replace '<<PARENT>>', "dbosoft/$OSVersion-standard/starter" `
    -replace '<<SSH_PUBLIC_KEY>>', "$(egs-tool get-ssh-key)"

  if (-not $PublishPath) {
    $PublishPath = "$PSScriptRoot/../../src/Eryph.GuestServices.Service/bin/Release/net10.0/win-x64/publish"
  }
  $resolvedPublishPath = (Resolve-Path -LiteralPath $PublishPath).Path

  if (-not $ProbePath) {
    $ProbePath = "$PSScriptRoot/EgsShellProbe/bin/Release/net10.0-windows/win-x64/publish/egs-shell-probe.exe"
  }
  $resolvedProbePath = (Resolve-Path -LiteralPath $ProbePath).Path

  $project = New-TestProject
  $catletName = New-CatletName
  Write-Host "Creating catlet $catletName in project $($project.Name) ..."

  $catlet = New-Catlet -Config $catletConfig -Name $catletName `
    -ProjectName $project.Name -SkipVariablesPrompt
  $hostName = "$($catlet.Id).eryph.alt"

  Connect-Catlet -Id $catlet.Id
  Update-EgsService -Catlet $catlet -PublishPath $resolvedPublishPath
}

AfterAll {
  # Set $env:EGS_E2E_KEEP_VM=1 to keep the catlet for post-mortem inspection.
  if ($env:EGS_E2E_KEEP_VM) {
    Write-Host "EGS_E2E_KEEP_VM is set — leaving catlet $($catlet.Name) (project $($project.Name)) for inspection."
    return
  }
  if ($catlet) {
    Remove-Catlet -Id $catlet.Id -Force -ErrorAction SilentlyContinue
  }
  if ($project) {
    Remove-EryphProject -Id $project.Id -Force -ErrorAction SilentlyContinue
  }
}

Describe 'Configurable shell' {

  AfterEach {
    # Make sure no shell override leaks across tests.
    egs-tool set-shell $catlet.VmId --reset | Out-Null
  }

  Context 'set-shell tool command' {

    It 'writes shell and shell-args to the External KVP pool' {
      # Spectre.Console.Cli treats `--arguments -NoLogo` as two flags
      # (the value '-NoLogo' is interpreted as an option). The `=` form
      # binds the value unambiguously.
      egs-tool set-shell $catlet.VmId --command 'pwsh.exe' --arguments='-NoLogo -NoProfile' | Out-Null

      $data = egs-tool get-data --json $catlet.VmId | ConvertFrom-Json -AsHashtable
      $data.external.'eryph:guest-services:shell' | Should -Be 'pwsh.exe'
      $data.external.'eryph:guest-services:shell-args' | Should -Be '-NoLogo -NoProfile'
    }

    It 'writes only shell when no arguments are passed' {
      egs-tool set-shell $catlet.VmId --command 'pwsh.exe' | Out-Null

      $data = egs-tool get-data --json $catlet.VmId | ConvertFrom-Json -AsHashtable
      $data.external.'eryph:guest-services:shell' | Should -Be 'pwsh.exe'
      $data.external.ContainsKey('eryph:guest-services:shell-args') | Should -BeFalse
    }

    It '--reset clears both keys' {
      egs-tool set-shell $catlet.VmId --command 'pwsh.exe' --arguments='-x' | Out-Null
      egs-tool set-shell $catlet.VmId --reset | Out-Null

      $data = egs-tool get-data --json $catlet.VmId | ConvertFrom-Json -AsHashtable
      $data.external.ContainsKey('eryph:guest-services:shell') | Should -BeFalse
      $data.external.ContainsKey('eryph:guest-services:shell-args') | Should -BeFalse
    }
  }

  # The shell-selection tests use the EgsShellProbe instead of ssh.exe. ssh
  # in Win32-OpenSSH writes channel output to the Windows console buffer, not
  # stdout, so a redirected ssh.exe captures nothing. The probe drives the
  # SSH protocol directly via Microsoft.DevTunnels.Ssh and pumps channel
  # output to its stdout, which a script can capture normally.
  # Shell-selection assertions match against the shell's startup banner:
  #   - powershell.exe (Windows PowerShell 5.1) prints "Windows PowerShell"
  #   - pwsh.exe       (PowerShell 7+)          prints "PowerShell 7."
  # This avoids depending on driving input through PSReadLine over a
  # freshly-allocated ConPTY pipe, which is unreliable. Patterns are
  # inlined per test because Pester v5 doesn't propagate variables defined
  # in a Context body to the run-phase It blocks.
  Context 'shell selection' {

    It 'spawns Windows PowerShell when no override is set' {
      $output = Invoke-EgsShellProbe -ProbePath $resolvedProbePath -VmId $catlet.VmId `
        -TimeoutMs 6000

      $output | Should -Match 'Windows PowerShell'
      $output | Should -Not -Match 'PowerShell 7\.'
    }

    It 'spawns pwsh when KVP override is set' {
      egs-tool set-shell $catlet.VmId --command 'pwsh.exe' | Out-Null

      $output = Invoke-EgsShellProbe -ProbePath $resolvedProbePath -VmId $catlet.VmId `
        -TimeoutMs 6000

      $output | Should -Match 'PowerShell 7\.'
    }

    It 'spawns pwsh when SSH-sent SHELL env var is set (no KVP override)' {
      $output = Invoke-EgsShellProbe -ProbePath $resolvedProbePath -VmId $catlet.VmId `
        -SetEnv @{ SHELL = 'pwsh.exe' } `
        -TimeoutMs 6000

      $output | Should -Match 'PowerShell 7\.'
    }

    It 'KVP override wins over SSH-sent SHELL env' {
      egs-tool set-shell $catlet.VmId --command 'pwsh.exe' | Out-Null

      $output = Invoke-EgsShellProbe -ProbePath $resolvedProbePath -VmId $catlet.VmId `
        -SetEnv @{ SHELL = 'powershell.exe' } `
        -TimeoutMs 6000

      $output | Should -Match 'PowerShell 7\.'
    }

    It 'returns to default after --reset' {
      egs-tool set-shell $catlet.VmId --command 'pwsh.exe' | Out-Null
      egs-tool set-shell $catlet.VmId --reset | Out-Null

      $output = Invoke-EgsShellProbe -ProbePath $resolvedProbePath -VmId $catlet.VmId `
        -TimeoutMs 6000

      $output | Should -Match 'Windows PowerShell'
      $output | Should -Not -Match 'PowerShell 7\.'
    }
  }
}
