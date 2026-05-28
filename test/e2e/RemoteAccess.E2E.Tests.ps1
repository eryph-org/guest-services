#Requires -Version 7.4
#Requires -RunAsAdministrator
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
  $adminPassword = 'RaE2e!Pw9_Throwaway'

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
    Write-Host "egs-service is available — refreshing SSH config aliases ..."
    # Refreshes ~/.ssh/config entries (IdentityFile pointing at the host's
    # local egs-tool private key). No KVP write here — that lives in
    # add-ssh-config and the per-test contexts decide whether to call it.
    egs-tool update-ssh-config | Out-Null
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
      # The flag is read on every auth attempt — no service restart needed.
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
}
