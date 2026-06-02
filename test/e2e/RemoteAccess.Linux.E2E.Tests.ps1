#Requires -Version 7.4
#Requires -RunAsAdministrator
#Requires -Module Pester
#Requires -Module Eryph.ComputeClient
<#
.SYNOPSIS
  E2E for the egs remote-access auth path on LINUX catlets.

.NOTES
  Mirror of RemoteAccess.E2E.Tests.ps1 (the Windows version), exercising
  the same code (multi-key reader, KvpAuthEnabled gate, dual-write
  add-ssh-config) end-to-end on Linux.

  Architectural deltas from the Windows suite:
    - No offline VHD mount. Catlet boots with the parent gene's
      pre-installed (older) egs-service. BeforeAll ssh-es in,
      stops/swaps/restarts to put the local build under test.
    - Admin channel is direct sshd (Linux has no Hyper-V VMBus PSDirect).
      The linux-starter fodder gene installs the host's egs-tool public
      key in the OS user's authorized_keys for this channel.
    - Hardening flag is /etc/opt/eryph/guest-services/service-control.conf
      (KEY=VALUE) not the Windows registry.
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

  # Channels (cf. the Windows e2e header):
  #   1. egs-service over vsock (egs-tool proxy) — system under test.
  #        Public key  -> catlet's /etc/opt/eryph/guest-services/id_egs.pub
  #                       (written below via the admin sshd channel after
  #                        the binary swap)
  #        Private key -> host's C:\ProgramData\eryph\guest-services\private\id_egs
  #   2. Direct sshd as $adminUsername — admin channel for binary swap,
  #      service-control.conf edits, systemctl restart. Independent of
  #      eryph-guest-services lifecycle.
  $hostEgsPublicKey = (egs-tool get-ssh-key).Trim()
  if (-not $hostEgsPublicKey) {
    throw "egs-tool get-ssh-key returned empty — run 'egs-tool initialize' first."
  }
  $adminUsername = 'e2euser'
  # Throwaway password generated per run — never persisted outside this
  # catlet (which is destroyed in AfterAll). Generated rather than
  # hardcoded so secret scanners don't false-positive on the source.
  $adminPassword = New-ThrowawayPassword

  $catletConfigTemplate = Get-Content -Raw -Path $PSScriptRoot/remote-access-linux-catlet.yaml
  $catletConfig = $catletConfigTemplate `
    -replace '<<PARENT>>', "dbosoft/$OSVersion/starter"

  $project = New-TestProject
  $catletName = New-CatletName
  Write-Host "Creating BASE catlet $catletName in project $($project.Name) ..."
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

  # Restart so the freshly-written id_egs.pub is picked up (egs-service
  # reads it on startup, same as on Windows).
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
    # No SSH-config alias / KVP key write here — these tests connect via the
    # egs proxy with an explicit -i identity and -F NUL (bypassing
    # ~/.ssh/config). The per-test contexts decide whether to call
    # add-ssh-config, which performs the KVP client-key push.
  }
  finally {
    $PSNativeCommandUseErrorActionPreference = $savedPref
  }

  $provisionedKey = Join-Path $env:ProgramData 'eryph\guest-services\private\id_egs'
  if (-not (Test-Path -LiteralPath $provisionedKey)) {
    throw "Local egs-tool key not found at $provisionedKey; run 'egs-tool initialize' first."
  }
}

AfterAll {
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

Describe 'Remote-access client auth (Linux)' {

  Context 'baseline' {

    It 'provisioned id_egs.pub authorizes' {
      $exit = Test-CatletSshWithKey -VmId $catlet.VmId -IdentityFile $provisionedKey
      $exit | Should -Be 0
    }
  }

  Context 'side-by-side: KVP-pushed key authorizes alongside provisioned key' {

    BeforeAll {
      $ephemeral = New-EphemeralSshKey -Comment 'remote-access-linux-e2e-named-slot'
      Push-CatletExternalKvp -VmId $catlet.VmId `
        -Key "eryph:guest-services:client-public-key:remote-access-linux-e2e-named" `
        -Value $ephemeral.PublicKey
    }

    AfterAll {
      if ($ephemeral) {
        Remove-Item -LiteralPath $ephemeral.TempDir -Recurse -Force -ErrorAction SilentlyContinue
      }
    }

    It 'ephemeral key in named slot authorizes' {
      $exit = Test-CatletSshWithKey -VmId $catlet.VmId -IdentityFile $ephemeral.PrivateKeyPath
      $exit | Should -Be 0
    }

    It 'provisioned key still authorizes (cache + KVP both in the set)' {
      $exit = Test-CatletSshWithKey -VmId $catlet.VmId -IdentityFile $provisionedKey
      $exit | Should -Be 0
    }
  }

  Context 'add-ssh-config dual-write reaches both slots' {

    BeforeAll {
      egs-tool add-ssh-config $catlet.VmId | Out-Null
    }

    It 'writes both legacy and named slot for this host' {
      $data = egs-tool get-data --json $catlet.VmId | ConvertFrom-Json -AsHashtable
      $hostSlot = "eryph:guest-services:client-public-key:$($env:COMPUTERNAME.ToLowerInvariant())"
      $data.external.ContainsKey('eryph:guest-services:client-public-key') | Should -BeTrue
      $data.external.ContainsKey($hostSlot) | Should -BeTrue
      $data.external['eryph:guest-services:client-public-key'] | Should -Be $data.external[$hostSlot]
    }

    It 'host can still ssh with its egs-tool key after the dual-write' {
      $exit = Test-CatletSshWithKey -VmId $catlet.VmId -IdentityFile $provisionedKey
      $exit | Should -Be 0
    }
  }

  Context 'KvpAuthEnabled hardening flag (Linux service-control.conf)' {

    BeforeAll {
      $hardeningEphemeral = New-EphemeralSshKey -Comment 'remote-access-linux-e2e-hardening'
      Push-CatletExternalKvp -VmId $catlet.VmId `
        -Key "eryph:guest-services:client-public-key:remote-access-linux-e2e-hardening" `
        -Value $hardeningEphemeral.PublicKey
    }

    AfterAll {
      try {
        Set-CatletKvpAuthEnabledLinux -Catlet $catlet -Username $adminUsername -Value $null
      } catch { }
      if ($hardeningEphemeral) {
        Remove-Item -LiteralPath $hardeningEphemeral.TempDir -Recurse -Force -ErrorAction SilentlyContinue
      }
    }

    It 'KVP-pushed key authorizes when KvpAuthEnabled is unset (default ON)' {
      Set-CatletKvpAuthEnabledLinux -Catlet $catlet -Username $adminUsername -Value $null
      $exit = Test-CatletSshWithKey -VmId $catlet.VmId -IdentityFile $hardeningEphemeral.PrivateKeyPath
      $exit | Should -Be 0
    }

    It 'KvpAuthEnabled=0 rejects KVP-pushed key' {
      Set-CatletKvpAuthEnabledLinux -Catlet $catlet -Username $adminUsername -Value 0
      $exit = Test-CatletSshWithKey -VmId $catlet.VmId -IdentityFile $hardeningEphemeral.PrivateKeyPath
      $exit | Should -Not -Be 0
    }

    It 'KvpAuthEnabled=0 keeps the provisioned key working (only KVP is gated)' {
      Set-CatletKvpAuthEnabledLinux -Catlet $catlet -Username $adminUsername -Value 0
      $exit = Test-CatletSshWithKey -VmId $catlet.VmId -IdentityFile $provisionedKey
      $exit | Should -Be 0
    }

    It 'KvpAuthEnabled=1 restores KVP-pushed key authorization' {
      Set-CatletKvpAuthEnabledLinux -Catlet $catlet -Username $adminUsername -Value 1
      $exit = Test-CatletSshWithKey -VmId $catlet.VmId -IdentityFile $hardeningEphemeral.PrivateKeyPath
      $exit | Should -Be 0
    }

    It 'deleting the value (back to default) keeps KVP keys authorized' {
      Set-CatletKvpAuthEnabledLinux -Catlet $catlet -Username $adminUsername -Value $null
      $exit = Test-CatletSshWithKey -VmId $catlet.VmId -IdentityFile $hardeningEphemeral.PrivateKeyPath
      $exit | Should -Be 0
    }
  }
}
