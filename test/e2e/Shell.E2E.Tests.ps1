#Requires -Version 7.4
#Requires -Module Pester
#Requires -Module Eryph.ComputeClient
param (
    [Parameter()]
    [ValidateSet('winsrv2019', 'winsrv2022', 'winsrv2025')]
    [string] $OSVersion = 'winsrv2022',

    [Parameter()]
    [string] $PublishPath
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

  # The shell-selection tests below need to drive an interactive shell session
  # (channel: pty-req + env + shell). Win32-OpenSSH's ssh.exe with `-tt` and
  # redirected stdin silently refuses to send pty-req — the channel opens and
  # closes immediately without ever invoking ShellService. Properly testing
  # this end-to-end requires a custom probe built on Microsoft.DevTunnels.Ssh
  # (e.g. a small `EgsShellProbe` console app). The selector logic itself is
  # covered exhaustively by the unit tests in
  # `Eryph.GuestServices.DevTunnels.Ssh.Extensions.Tests` and
  # `Eryph.GuestServices.Service.Tests`.
  Context 'shell selection' -Skip {

    It 'spawns Windows PowerShell when no override is set' {
      $output = Invoke-InteractiveShell -HostName $hostName `
        -InputLines @('"EGS-MARKER-START:" + (Get-Process -Id $PID).ProcessName + ":EGS-MARKER-END"')

      $output | Should -Match 'EGS-MARKER-START:powershell:EGS-MARKER-END'
    }

    It 'spawns pwsh when KVP override is set' {
      egs-tool set-shell $catlet.VmId --command 'pwsh.exe' | Out-Null

      $output = Invoke-InteractiveShell -HostName $hostName `
        -InputLines @('"EGS-MARKER-START:" + (Get-Process -Id $PID).ProcessName + ":EGS-MARKER-END"')

      $output | Should -Match 'EGS-MARKER-START:pwsh:EGS-MARKER-END'
    }

    It 'spawns pwsh when SSH-sent SHELL env var is set (no KVP override)' {
      $output = Invoke-InteractiveShell -HostName $hostName `
        -SetEnv @{ SHELL = 'pwsh.exe' } `
        -InputLines @('"EGS-MARKER-START:" + (Get-Process -Id $PID).ProcessName + ":EGS-MARKER-END"')

      $output | Should -Match 'EGS-MARKER-START:pwsh:EGS-MARKER-END'
    }

    It 'KVP override wins over SSH-sent SHELL env' {
      egs-tool set-shell $catlet.VmId --command 'pwsh.exe' | Out-Null

      # Try to override with a different shell via env — KVP must win.
      $output = Invoke-InteractiveShell -HostName $hostName `
        -SetEnv @{ SHELL = 'powershell.exe' } `
        -InputLines @('"EGS-MARKER-START:" + (Get-Process -Id $PID).ProcessName + ":EGS-MARKER-END"')

      $output | Should -Match 'EGS-MARKER-START:pwsh:EGS-MARKER-END'
    }

    It 'returns to default after --reset' {
      egs-tool set-shell $catlet.VmId --command 'pwsh.exe' | Out-Null
      egs-tool set-shell $catlet.VmId --reset | Out-Null

      $output = Invoke-InteractiveShell -HostName $hostName `
        -InputLines @('"EGS-MARKER-START:" + (Get-Process -Id $PID).ProcessName + ":EGS-MARKER-END"')

      $output | Should -Match 'EGS-MARKER-START:powershell:EGS-MARKER-END'
    }
  }
}
