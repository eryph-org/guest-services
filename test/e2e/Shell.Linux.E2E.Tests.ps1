#Requires -Version 7.4
#Requires -RunAsAdministrator
#Requires -Module Pester
#Requires -Module Eryph.ComputeClient
<#
.SYNOPSIS
  E2E for the SSH `exec` (remote command) path and configurable shell on
  LINUX catlets.

.NOTES
  Linux counterpart of the exec coverage in Shell.E2E.Tests.ps1 (Windows).
  Exercises the same server-side path — CommandService -> CommandForwarder
  -> IShellSelector — on linux-x64: the remote command runs through the
  selected shell (`<shell> -c "<command>"`), the KVP shell override is
  honored, the remote exit code propagates, and stderr is merged back onto
  the channel.

  Deploy model mirrors RemoteAccess.Linux.E2E.Tests.ps1: no offline VHD
  mount. The catlet boots with the parent gene's (older) egs-service;
  BeforeAll swaps in the local build over the direct-sshd admin channel,
  writes id_egs.pub, and restarts the unit.
#>
param (
    [Parameter()]
    [string] $OSVersion = 'ubuntu-24.04',

    [Parameter()]
    [string] $PublishPath
)

BeforeAll {
  $PSNativeCommandUseErrorActionPreference = $true
  $ErrorActionPreference = 'Stop'
  . $PSScriptRoot/Helpers.ps1

  if (-not $PublishPath) {
    $PublishPath = "$PSScriptRoot/../../src/Eryph.GuestServices.Service/bin/Release/net10.0/linux-x64/publish"
  }
  $resolvedPublishPath = (Resolve-Path -LiteralPath $PublishPath).Path

  # Channels (cf. RemoteAccess.Linux.E2E.Tests.ps1):
  #   1. egs-service over vsock (egs-tool proxy) — the system under test.
  #   2. Direct sshd as $adminUsername — admin channel for the binary swap,
  #      independent of eryph-guest-services lifecycle.
  $hostEgsPublicKey = (egs-tool get-ssh-key).Trim()
  if (-not $hostEgsPublicKey) {
    throw "egs-tool get-ssh-key returned empty — run 'egs-tool initialize' first."
  }
  $adminUsername = 'e2euser'
  $adminPassword = New-ThrowawayPassword

  $catletConfigTemplate = Get-Content -Raw -Path $PSScriptRoot/remote-access-linux-catlet.yaml
  $catletConfig = $catletConfigTemplate `
    -replace '<<PARENT>>', "dbosoft/$OSVersion/starter"

  $project = New-TestProject
  $catletName = New-CatletName
  Write-Host "Creating catlet $catletName in project $($project.Name) ..."
  $catlet = New-Catlet -Config $catletConfig -Name $catletName `
    -ProjectName $project.Name `
    -Variables @{
      adminUsername = $adminUsername
      adminPassword = $adminPassword
      sshPublicKey = $hostEgsPublicKey
    } `
    -SkipVariablesPrompt

  Write-Host "Starting catlet $($catlet.Name) ..."
  Start-Catlet -Id $catlet.Id -Force

  Write-Host "Waiting for direct sshd as '$adminUsername' ..."
  Wait-LinuxCatletSshReady -Catlet $catlet -Username $adminUsername `
    -Timeout (New-TimeSpan -Minutes 10)

  Write-Host "Replacing egs-service binaries online (publish=$resolvedPublishPath) ..."
  Update-EgsServiceLinuxOnline -Catlet $catlet -Username $adminUsername `
    -PublishPath $resolvedPublishPath

  Write-Host "Writing host's egs-tool public key into /etc/opt/eryph/guest-services/id_egs.pub ..."
  Write-LinuxEgsClientKey -Catlet $catlet -Username $adminUsername `
    -PublicKey $hostEgsPublicKey

  Invoke-CatletAdminSshLinux -Catlet $catlet -Username $adminUsername `
    -Command 'sudo systemctl restart eryph-guest-services' 2>&1 | Out-Null

  Write-Host "Waiting for egs-service SSH listener (get-status=available) ..."
  $savedPref = $PSNativeCommandUseErrorActionPreference
  $PSNativeCommandUseErrorActionPreference = $false
  try {
    Wait-Assert -Timeout (New-TimeSpan -Minutes 5) -Interval (New-TimeSpan -Seconds 3) {
      $status = egs-tool get-status $catlet.VmId
      if ($status -ne 'available') { throw "guest services not available: $status" }
    }
    Write-Host "egs-service is available."
  }
  finally {
    $PSNativeCommandUseErrorActionPreference = $savedPref
  }

  # The exec tests connect over the egs proxy via the *.hyper-v.alt Host
  # block (User egs + IdentityFile + ProxyCommand) that add-ssh-config
  # writes — same alias the Windows Shell suite uses.
  egs-tool add-ssh-config $catlet.VmId | Out-Null
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

Describe 'Configurable shell and exec (Linux)' {

  AfterEach {
    # Make sure no shell override leaks across tests.
    egs-tool set-shell $catlet.VmId --reset | Out-Null
  }

  # The SSH `exec` request (`ssh host "<command>"`) runs the command through
  # the selected shell — the OpenSSH `$SHELL -c "..."` contract — not as a
  # bare, split-on-space executable.
  Context 'exec (remote command)' {

    It 'runs a command through the default shell' {
      $result = Invoke-EgsSshCommand -VmId $catlet.VmId -Command 'echo egs-exec-ok'

      $result.ExitCode | Should -Be 0
      $result.Output | Should -Match 'egs-exec-ok'
    }

    It 'evaluates a pipeline (shell parsing, not a split-on-space exec)' {
      # A pipe only works if a shell interprets the command. The pre-fix exec
      # path tried to launch an executable literally named "echo".
      $result = Invoke-EgsSshCommand -VmId $catlet.VmId `
        -Command 'echo hello | tr a-z A-Z'

      $result.ExitCode | Should -Be 0
      $result.Output | Should -Match 'HELLO'
    }

    It 'propagates the remote exit code' {
      $result = Invoke-EgsSshCommand -VmId $catlet.VmId -Command 'exit 7'

      $result.ExitCode | Should -Be 7
    }

    It 'returns stderr to the client' {
      # stderr is merged onto the channel (the library has no extended-data
      # support). Both markers must come back; pre-fix the stderr one was lost.
      $result = Invoke-EgsSshCommand -VmId $catlet.VmId `
        -Command 'echo egs-stderr-marker 1>&2; echo egs-stdout-marker'

      $result.ExitCode | Should -Be 0
      $result.Output | Should -Match 'egs-stdout-marker'
      $result.Output | Should -Match 'egs-stderr-marker'
    }
  }

  # The exec path resolves its shell through the same KVP override as the
  # interactive path. Switching the override changes which shell runs the
  # command — proven here by toggling a bash-only variable.
  Context 'configurable shell for exec' {

    It 'runs exec through bash when set as the override' {
      egs-tool set-shell $catlet.VmId --command '/bin/bash' | Out-Null

      $result = Invoke-EgsSshCommand -VmId $catlet.VmId -Command 'echo $BASH_VERSION'

      $result.ExitCode | Should -Be 0
      # bash exports BASH_VERSION (e.g. "5.2.21(1)-release").
      $result.Output | Should -Match '\d+\.\d+'
    }

    It 'runs exec through /bin/sh when set as the override' {
      egs-tool set-shell $catlet.VmId --command '/bin/sh' | Out-Null

      $result = Invoke-EgsSshCommand -VmId $catlet.VmId -Command 'echo $BASH_VERSION'

      $result.ExitCode | Should -Be 0
      # /bin/sh (dash on Ubuntu) has no BASH_VERSION — proves the override is
      # actually used for exec, not just the ambient default.
      $result.Output | Should -BeNullOrEmpty
    }
  }
}
