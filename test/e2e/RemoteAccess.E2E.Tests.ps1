#Requires -Version 7.4
#Requires -RunAsAdministrator
#Requires -Module Pester
#Requires -Module Eryph.ComputeClient
<#
.SYNOPSIS
  E2E for the egs remote-access auth path on WINDOWS catlets.

.NOTES
  Linux coverage lives in RemoteAccess.Linux.E2E.Tests.ps1 (same assertion
  surface, different parent gene + online binary swap via direct sshd
  instead of offline VHD mount + PsDirect).
#>
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

  if (-not $PublishPath) {
    $PublishPath = "$PSScriptRoot/../../src/Eryph.GuestServices.Service/bin/Release/net10.0/win-x64/publish"
  }
  $resolvedPublishPath = (Resolve-Path -LiteralPath $PublishPath).Path

  # Channels in this suite:
  #
  #   1. egs-service over vsock (egs-tool proxy) — the system under test.
  #        Public key  -> catlet's C:\ProgramData\eryph\guest-services\id_egs.pub
  #                       (written below via the offline VHD mount)
  #        Private key -> host's C:\ProgramData\eryph\guest-services\private\id_egs
  #                       (used as IdentityFile by every test below)
  #
  #   2. PowerShell Direct (Hyper-V VMBus) — the admin channel for any
  #      operation that must SURVIVE a stop/start of eryph-guest-services
  #      (notably, the restart triggered by KvpAuthEnabled changes). It
  #      uses a hardcoded throwaway Administrator password set via
  #      cloud-config below; the catlet is destroyed in AfterAll.
  $hostEgsPublicKey = (egs-tool get-ssh-key).Trim()
  if (-not $hostEgsPublicKey) {
    throw "egs-tool get-ssh-key returned empty — run 'egs-tool initialize' first."
  }
  # Throwaway Administrator password generated per run — never persisted
  # outside this catlet (which is destroyed in AfterAll). Generated rather
  # than hardcoded so secret scanners don't false-positive on the source.
  $adminPassword = New-ThrowawayPassword

  $catletConfigTemplate = Get-Content -Raw -Path $PSScriptRoot/remote-access-catlet.yaml
  $catletConfig = $catletConfigTemplate `
    -replace '<<PARENT>>', "dbosoft/$OSVersion-standard/starter"

  $project = New-TestProject
  $catletName = New-CatletName
  Write-Host "Creating BASE catlet $catletName in project $($project.Name) ..."
  $catlet = New-Catlet -Config $catletConfig -Name $catletName `
    -ProjectName $project.Name `
    -Variables @{ adminPassword = $adminPassword } `
    -SkipVariablesPrompt
  $script:CatletAdminCredential = New-Object System.Management.Automation.PSCredential('Administrator',
    (ConvertTo-SecureString $adminPassword -AsPlainText -Force))

  $state = (Get-VM -Id $catlet.VmId).State
  if ($state -ne 'Off') { throw "Expected catlet to be Off after creation; got $state." }

  Write-Host "Replacing egs-service binaries offline (publish=$resolvedPublishPath) ..."
  Update-EgsServiceBinariesOffline -VmId $catlet.VmId -PublishPath $resolvedPublishPath

  Write-Host "Writing host's egs-tool public key into offline VHD as id_egs.pub ..."
  Write-OfflineEgsClientKey -VmId $catlet.VmId -PublicKey $hostEgsPublicKey

  Write-Host "Starting catlet $($catlet.Name) ..."
  Start-Catlet -Id $catlet.Id -Force

  Write-Host "Waiting for provisioning to complete (KVP eryph.provisioning.state) ..."
  $finalState = Wait-ForProvisioningComplete -VmId $catlet.VmId `
    -Timeout (New-TimeSpan -Minutes 10)
  Write-Host "Provisioning state: $finalState"

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

  # Path to the host's egs-tool private key — the matching half of the
  # public key we just wrote into the catlet's id_egs.pub. Used as the
  # "provisioned identity" in the tests below.
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

Describe 'Remote-access client auth' {

  Context 'baseline' {

    It 'provisioned (offline-written id_egs.pub) authorizes' {
      # Anchors the rest of the suite. Uses the on-disk client-key path
      # exclusively — no KVP entry has been written yet.
      $exit = Test-CatletSshWithKey -VmId $catlet.VmId -IdentityFile $provisionedKey
      $exit | Should -Be 0
    }
  }

  Context 'side-by-side: KVP-pushed key authorizes alongside provisioned key' {

    BeforeAll {
      # Push an ephemeral keypair into a named slot in the External KVP
      # pool — a key the catlet has never seen on disk. Pre-PR behaviour
      # would return the cached/provisioned key and skip KVP entirely;
      # post-PR both sources must contribute to the authorized set.
      $ephemeral = New-EphemeralSshKey -Comment 'remote-access-e2e-named-slot'
      Push-CatletExternalKvp -VmId $catlet.VmId `
        -Key "eryph:guest-services:client-public-key:remote-access-e2e-named" `
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
      # The host-side add-ssh-config writer dual-writes legacy +
      # ":<hostname>" slots. After it runs the catlet's External KVP must
      # show both.
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

  Context 'KvpAuthEnabled hardening flag' {

    BeforeAll {
      $hardeningEphemeral = New-EphemeralSshKey -Comment 'remote-access-e2e-hardening'
      Push-CatletExternalKvp -VmId $catlet.VmId `
        -Key "eryph:guest-services:client-public-key:remote-access-e2e-hardening" `
        -Value $hardeningEphemeral.PublicKey
    }

    AfterAll {
      # Make sure the flag is cleared even if a test bails mid-way.
      try { Set-CatletKvpAuthEnabled -Catlet $catlet -Value $null -Credential $script:CatletAdminCredential } catch { }
      if ($hardeningEphemeral) {
        Remove-Item -LiteralPath $hardeningEphemeral.TempDir -Recurse -Force -ErrorAction SilentlyContinue
      }
    }

    It 'KVP-pushed key authorizes when KvpAuthEnabled is unset (default ON)' {
      Set-CatletKvpAuthEnabled -Catlet $catlet -Value $null -Credential $script:CatletAdminCredential
      $exit = Test-CatletSshWithKey -VmId $catlet.VmId -IdentityFile $hardeningEphemeral.PrivateKeyPath
      $exit | Should -Be 0
    }

    It 'KvpAuthEnabled=0 rejects KVP-pushed key' {
      Set-CatletKvpAuthEnabled -Catlet $catlet -Value 0 -Credential $script:CatletAdminCredential
      # Set-CatletKvpAuthEnabled writes the registry, then restarts the
      # eryph-guest-services unit via PsDirect — the flag is cached at
      # service start, not re-read on every auth attempt.
      $exit = Test-CatletSshWithKey -VmId $catlet.VmId -IdentityFile $hardeningEphemeral.PrivateKeyPath
      $exit | Should -Not -Be 0
    }

    It 'KvpAuthEnabled=0 keeps the provisioned key working (only KVP is gated)' {
      Set-CatletKvpAuthEnabled -Catlet $catlet -Value 0 -Credential $script:CatletAdminCredential
      $exit = Test-CatletSshWithKey -VmId $catlet.VmId -IdentityFile $provisionedKey
      $exit | Should -Be 0
    }

    It 'KvpAuthEnabled=1 restores KVP-pushed key authorization' {
      Set-CatletKvpAuthEnabled -Catlet $catlet -Value 1 -Credential $script:CatletAdminCredential
      $exit = Test-CatletSshWithKey -VmId $catlet.VmId -IdentityFile $hardeningEphemeral.PrivateKeyPath
      $exit | Should -Be 0
    }

    It 'deleting the value (back to default) keeps KVP keys authorized' {
      Set-CatletKvpAuthEnabled -Catlet $catlet -Value $null -Credential $script:CatletAdminCredential
      $exit = Test-CatletSshWithKey -VmId $catlet.VmId -IdentityFile $hardeningEphemeral.PrivateKeyPath
      $exit | Should -Be 0
    }
  }

  Context 'PortForwardingEnabled gate' {

    # Port forwarding is the lone OPT-IN switch: off unless explicitly turned
    # on. The guest runs a loopback TCP probe; the host opens `ssh -L` through
    # the egs proxy to 127.0.0.1:<probe> inside the guest and reads the token.
    # That exercises the same direct-tcpip path a jump host uses.

    BeforeAll {
      $script:FwdGuestPort = 28080
      $script:FwdToken = "egs-portfwd-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
      Start-CatletLoopbackTcpProbe -Catlet $catlet -Port $script:FwdGuestPort `
        -Token $script:FwdToken -Credential $script:CatletAdminCredential
    }

    AfterAll {
      # When keeping the VM for inspection, leave forwarding ON and the probe
      # running so `ssh -L` can be debugged by hand against the kept catlet.
      if ($env:EGS_E2E_KEEP_VM) { return }
      try {
        Set-CatletServiceControlFlag -Catlet $catlet -Name 'PortForwardingEnabled' `
          -Value $null -Credential $script:CatletAdminCredential
      } catch { }
      try {
        Stop-CatletLoopbackTcpProbe -Catlet $catlet -Credential $script:CatletAdminCredential
      } catch { }
    }

    It 'does not advertise the feature and refuses -L when unset (default OFF)' {
      Set-CatletServiceControlFlag -Catlet $catlet -Name 'PortForwardingEnabled' `
        -Value $null -Credential $script:CatletAdminCredential

      $data = egs-tool get-data --json $catlet.VmId | ConvertFrom-Json -AsHashtable
      ($data.guest['eryph:guest-services:features'] -split ' ') | Should -Not -Contain 'port-forwarding'

      (Test-CatletPortForward -VmId $catlet.VmId -IdentityFile $provisionedKey `
        -GuestPort $script:FwdGuestPort -ExpectedToken $script:FwdToken) | Should -BeFalse
    }

    It 'advertises the feature and forwards -L when PortForwardingEnabled=1' {
      Set-CatletServiceControlFlag -Catlet $catlet -Name 'PortForwardingEnabled' `
        -Value 1 -Credential $script:CatletAdminCredential

      $data = egs-tool get-data --json $catlet.VmId | ConvertFrom-Json -AsHashtable
      ($data.guest['eryph:guest-services:features'] -split ' ') | Should -Contain 'port-forwarding'

      (Test-CatletPortForward -VmId $catlet.VmId -IdentityFile $provisionedKey `
        -GuestPort $script:FwdGuestPort -ExpectedToken $script:FwdToken) | Should -BeTrue
    }

    It 'refuses -L again when set back to 0' {
      Set-CatletServiceControlFlag -Catlet $catlet -Name 'PortForwardingEnabled' `
        -Value 0 -Credential $script:CatletAdminCredential

      (Test-CatletPortForward -VmId $catlet.VmId -IdentityFile $provisionedKey `
        -GuestPort $script:FwdGuestPort -ExpectedToken $script:FwdToken) | Should -BeFalse
    }
  }
}
