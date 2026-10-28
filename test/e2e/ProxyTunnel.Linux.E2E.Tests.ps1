#Requires -Version 7.4
#Requires -RunAsAdministrator
#Requires -Module Pester
#Requires -Module Eryph.ComputeClient
<#
.SYNOPSIS
  E2E for `egs-tool catlet tunnel` — the policy-driven proxied tunnel — on a
  LINUX catlet, driven over the eryph-authorized channel (NOT the VM hvsocket
  proxy the RemoteAccess suites use).

.NOTES
  First catlet-channel E2E suite. BeforeAll provisions a catlet, swaps in the
  local egs-service build (the parent gene ships an older one without PR #75
  forwarding), turns PortForwardingEnabled on, and authorizes this operator's
  managed client key in the guest via `egs-tool catlet add-key` (the eryph
  compute-API flow). The tunnel itself then runs in-process in egs-tool.

  Channels:
    1. eryph channel (egs-tool catlet ...) — the system under test.
    2. Direct sshd as the admin user — for binary swap, service-control.conf
       edits, and in-guest probes. Independent of eryph-guest-services.
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

  $script:EgsTool = (Get-Command egs-tool).Source
  Write-Host "Using egs-tool: $script:EgsTool"

  $hostEgsPublicKey = (egs-tool get-ssh-key).Trim()
  if (-not $hostEgsPublicKey) { throw "egs-tool get-ssh-key returned empty — run 'egs-tool initialize' first." }

  $adminUsername = 'e2euser'
  $adminPassword = New-ThrowawayPassword

  $catletConfig = (Get-Content -Raw -Path $PSScriptRoot/remote-access-linux-catlet.yaml) `
    -replace '<<PARENT>>', "dbosoft/$OSVersion/starter"

  $project = New-TestProject
  $catletName = New-CatletName
  Write-Host "Creating catlet $catletName in project $($project.Name) ..."
  $catlet = New-Catlet -Config $catletConfig -Name $catletName -ProjectName $project.Name `
    -Variables @{ adminUsername = $adminUsername; adminPassword = $adminPassword; sshPublicKey = $hostEgsPublicKey } `
    -SkipVariablesPrompt

  Write-Host "Starting catlet ..."
  Start-Catlet -Id $catlet.Id -Force
  Wait-LinuxCatletSshReady -Catlet $catlet -Username $adminUsername -Timeout (New-TimeSpan -Minutes 10)

  Write-Host "Swapping in the local egs-service build ..."
  Update-EgsServiceLinuxOnline -Catlet $catlet -Username $adminUsername -PublishPath $resolvedPublishPath

  Write-Host "Enabling PortForwardingEnabled (restarts egs-service) ..."
  Set-CatletServiceControlFlagLinux -Catlet $catlet -Username $adminUsername `
    -Name 'PortForwardingEnabled' -Value 1

  Write-Host "Waiting for the eryph channel (catlet get-status=available) ..."
  $savedPref = $PSNativeCommandUseErrorActionPreference
  $PSNativeCommandUseErrorActionPreference = $false
  try {
    Wait-Assert -Timeout (New-TimeSpan -Minutes 5) -Interval (New-TimeSpan -Seconds 3) {
      # get-status prints several lines; join them so -match tests the whole text
      # rather than the array of non-matching lines.
      $status = (& $script:EgsTool catlet get-status $catlet.Id | Out-String)
      if ($status -notmatch 'Guest services:\s*available') {
        throw "catlet guest services not available: $status"
      }
    }
  } finally { $PSNativeCommandUseErrorActionPreference = $savedPref }

  Write-Host "Authorizing this operator's managed key in the guest (catlet add-key) ..."
  & $script:EgsTool catlet add-key $catlet.Id
  if ($LASTEXITCODE -ne 0) { throw "catlet add-key failed with exit $LASTEXITCODE" }

  # Per-test scratch
  $script:PolicyDir = Join-Path $env:TEMP "egs-tunnel-e2e"
  New-Item -ItemType Directory -Force -Path $script:PolicyDir | Out-Null
}

AfterAll {
  if ($env:EGS_E2E_KEEP_VM) {
    Write-Host "EGS_E2E_KEEP_VM set — leaving catlet $($catlet.Name) (project $($project.Name))."
    return
  }
  if ($catlet) { Remove-Catlet -Id $catlet.Id -Force -ErrorAction SilentlyContinue }
  if ($project) { Remove-EryphProject -Id $project.Id -Force -ErrorAction SilentlyContinue }
}

Describe 'Proxied tunnel (Linux, eryph channel)' {

  Context 'egress — guest reaches a single host target through egs-tool' {
    BeforeAll {
      $script:EgToken = "egs-egress-$([guid]::NewGuid().ToString('N').Substring(0,8))"
      $script:EgHostProbe = Start-HostLoopbackTcpProbe -Token $script:EgToken
      $script:EgGuestPort = 23300
      $policy = @{
        tunnel = 'e2e-egress'; catlet = $catlet.Id
        egress = @(@{ name = 'probe'; listen = $script:EgGuestPort; to = "127.0.0.1:$($script:EgHostProbe.Port)" })
        keepalive = @{ idleSeconds = 10; intervalSeconds = 10; retries = 3 }
      }
      $script:EgPolicyFile = Join-Path $script:PolicyDir 'egress.json'
      ($policy | ConvertTo-Json -Depth 6) | Set-Content -Path $script:EgPolicyFile -Encoding utf8
      $script:EgTunnel = Start-CatletTunnel -EgsTool $script:EgsTool -PolicyFile $script:EgPolicyFile
    }
    AfterAll {
      Stop-CatletTunnel -Tunnel $script:EgTunnel
      if ($script:EgHostProbe) { Stop-HostLoopbackTcpProbe -Probe $script:EgHostProbe }
    }

    It 'relays a guest connection on the declared port to the single host target' {
      $token = Read-GuestLoopbackToken -Catlet $catlet -Username 'e2euser' -Port $script:EgGuestPort
      $token | Should -Be $script:EgToken
    }

    It 'opens no listener the policy did not declare' {
      $token = Read-GuestLoopbackToken -Catlet $catlet -Username 'e2euser' -Port 23999 -TolerateFailure
      $token | Should -Not -Be $script:EgToken
    }
  }

  Context 'egress — binds a distinct guest loopback address (listenAddress)' {
    BeforeAll {
      $script:EgaToken = "egs-egaddr-$([guid]::NewGuid().ToString('N').Substring(0,8))"
      $script:EgaHostProbe = Start-HostLoopbackTcpProbe -Token $script:EgaToken
      $policy = @{
        tunnel = 'e2e-egress-addr'; catlet = $catlet.Id
        egress = @(@{ name = 'probe'; listen = 23301; listenAddress = '127.0.0.2'; to = "127.0.0.1:$($script:EgaHostProbe.Port)" })
        keepalive = @{ idleSeconds = 10; intervalSeconds = 10; retries = 3 }
      }
      $file = Join-Path $script:PolicyDir 'egress-addr.json'
      ($policy | ConvertTo-Json -Depth 6) | Set-Content -Path $file -Encoding utf8
      $script:EgaTunnel = Start-CatletTunnel -EgsTool $script:EgsTool -PolicyFile $file
    }
    AfterAll {
      Stop-CatletTunnel -Tunnel $script:EgaTunnel
      if ($script:EgaHostProbe) { Stop-HostLoopbackTcpProbe -Probe $script:EgaHostProbe }
    }

    It 'relays a guest connection on 127.0.0.2 to the single host target' {
      $token = Read-GuestLoopbackToken -Catlet $catlet -Username 'e2euser' -Address '127.0.0.2' -Port 23301
      $token | Should -Be $script:EgaToken
    }
  }

  Context 'ingress — host reaches a single guest service through egs-tool' {
    BeforeAll {
      $script:InToken = "egs-ingress-$([guid]::NewGuid().ToString('N').Substring(0,8))"
      $script:InGuestPort = 23400
      Start-CatletLoopbackTcpProbeLinux -Catlet $catlet -Username 'e2euser' `
        -Port $script:InGuestPort -Token $script:InToken
      $script:InHostPort = Get-FreeLocalTcpPort
      $policy = @{
        tunnel = 'e2e-ingress'; catlet = $catlet.Id
        ingress = @(@{ name = 'probe'; listen = "127.0.0.1:$($script:InHostPort)"; to = "127.0.0.1:$($script:InGuestPort)" })
        keepalive = @{ idleSeconds = 5; intervalSeconds = 5; retries = 3 }
      }
      $script:InPolicyFile = Join-Path $script:PolicyDir 'ingress.json'
      ($policy | ConvertTo-Json -Depth 6) | Set-Content -Path $script:InPolicyFile -Encoding utf8
      $script:InTunnel = Start-CatletTunnel -EgsTool $script:EgsTool -PolicyFile $script:InPolicyFile
    }
    AfterAll {
      Stop-CatletTunnel -Tunnel $script:InTunnel
      Stop-CatletLoopbackTcpProbeLinux -Catlet $catlet -Username 'e2euser'
    }

    It 'relays a host connection on the declared port to the single guest service' {
      (Read-HostLoopbackToken -Port $script:InHostPort) | Should -Be $script:InToken
    }

    It 'stays up and still carries traffic after a long idle (keepalive)' {
      Start-Sleep -Seconds 25   # >> idleSeconds (5); the control channel must survive
      (Read-HostLoopbackToken -Port $script:InHostPort) | Should -Be $script:InToken
    }
  }

  Context 'gate — refused when PortForwardingEnabled is off' {
    AfterAll {
      Set-CatletServiceControlFlagLinux -Catlet $catlet -Username 'e2euser' `
        -Name 'PortForwardingEnabled' -Value 1
    }

    It 'fails closed (non-zero, no tunnel) when forwarding is off' {
      Set-CatletServiceControlFlagLinux -Catlet $catlet -Username 'e2euser' `
        -Name 'PortForwardingEnabled' -Value $null

      $probe = Start-HostLoopbackTcpProbe -Token 'should-not-matter'
      try {
        $policy = @{
          tunnel = 'e2e-gate'; catlet = $catlet.Id
          egress = @(@{ name = 'probe'; listen = 23500; to = "127.0.0.1:$($probe.Port)" })
          keepalive = @{ idleSeconds = 10; intervalSeconds = 10; retries = 3 }
        }
        $file = Join-Path $script:PolicyDir 'gate.json'
        ($policy | ConvertTo-Json -Depth 6) | Set-Content -Path $file -Encoding utf8

        $result = Invoke-CatletTunnelToCompletion -EgsTool $script:EgsTool -PolicyFile $file `
          -TimeoutSeconds 60
        $result.ExitCode | Should -Not -Be 0
        $result.Output | Should -Match 'port forwarding'
      } finally {
        Stop-HostLoopbackTcpProbe -Probe $probe
      }
    }
  }

  Context 'validate — rejects a malformed policy without connecting' {
    It 'rejects a wildcard target' {
      $file = Join-Path $script:PolicyDir 'bad.json'
      @'
{ "tunnel": "x", "catlet": "c",
  "egress": [ { "name": "e", "listen": 3300, "to": "*.corp:3300" } ],
  "keepalive": { "idleSeconds": 1, "intervalSeconds": 1, "retries": 1 } }
'@ | Set-Content -Path $file -Encoding utf8

      # A rejected policy exits non-zero by design; stop PowerShell from turning
      # that into a NativeCommandExitException before we can assert on it.
      $savedPref = $PSNativeCommandUseErrorActionPreference
      $PSNativeCommandUseErrorActionPreference = $false
      try {
        & $script:EgsTool catlet tunnel validate --policy $file 2>&1 | Out-Null
        $LASTEXITCODE | Should -Not -Be 0
      } finally {
        $PSNativeCommandUseErrorActionPreference = $savedPref
      }
    }
  }
}
