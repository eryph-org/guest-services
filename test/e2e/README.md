# e2e tests against a real eryph catlet

Two suites:

- `Shell.E2E.Tests.ps1` — configurable-shell tests; spins up a winsrv VM with
  the dbosoft/guest-services gene, post-boot patches the gene-installed
  egs-service, and exercises the SHELL selection chain (KVP > SSH env > defaults).
- `Provisioning.E2E.Tests.ps1` — embedded-provisioning tests; creates a BASE
  catlet (no fodder), mounts its VHD BEFORE first start, bakes our egs-service
  binary in, disables cloudbase-init, and asserts the embedded provisioning
  lifecycle runs cleanly at first boot via KVP and state.json.

The two suites have different goals so they're separate runners:

```powershell
pwsh ./Run-E2ETests.ps1                       # Shell suite, winsrv2022
pwsh ./Run-E2ETests.ps1 -OSVersion winsrv2025
pwsh ./Run-E2ETests.ps1 -SkipBuild            # reuse existing publish output
pwsh ./Run-ProvisioningE2ETests.ps1           # Provisioning suite — REQUIRES ADMIN
```

## Shell suite

End-to-end tests for the `set-shell` feature. They spin up a real Windows VM
via eryph, replace the gene-installed `egs-service` with the locally-built
binaries, and exercise the SHELL selection chain (KVP > SSH env > defaults).

## Prerequisites

- Windows host with Hyper-V enabled and eryph installed
- `egs-tool initialize` has been run (host-side SSH key + integration service
  registration)
- PowerShell 7.4+
- Network access to fetch the eryph genes
  (`dbosoft/winsrv2022-standard/starter`, `dbosoft/guest-services:win-install`,
  `dbosoft/powershell/1.2:win-install`) on first run

## Run

```powershell
pwsh ./Run-E2ETests.ps1                       # winsrv2022
pwsh ./Run-E2ETests.ps1 -OSVersion winsrv2025
pwsh ./Run-E2ETests.ps1 -SkipBuild            # reuse existing publish output
```

The script `dotnet publish`s the service in `Release/win-x64`, then invokes
Pester. A run takes roughly 8–15 minutes depending on whether the parent gene
is already cached.

## What gets tested

Eight tests in two groups:

- `set-shell tool command` (3): writes shell+shell-args, writes shell only
  when no arguments are passed, `--reset` clears both keys. Use
  `--arguments=-...` (with `=`) — Spectre.Console.Cli treats space-separated
  `-`-prefixed values as new flags.
- `shell selection` (5): default → `Windows PowerShell` banner; KVP override
  → `pwsh` banner; SSH-sent `SHELL` env var → `pwsh`; KVP wins over SSH env;
  `--reset` returns to default.

The shell-selection tests assert on the shell's startup banner
(`Windows PowerShell` for `powershell.exe`, `PowerShell 7.x` for `pwsh.exe`).
Banner matching avoids depending on driving input through PSReadLine, which
is unreliable over a freshly-allocated ConPTY pipe before the shell finishes
initializing.

### Why a custom SSH client is required

`ssh.exe -tt` (Win32-OpenSSH) writes channel output to the Windows console
buffer via `WriteConsole`, not to redirected stdout. Three independent
capture mechanisms (`Process.Start` redirection, PowerShell pipeline,
`cmd /c < > 2>&1`) all return empty for an interactive shell session.

`test/e2e/EgsShellProbe/` is a tiny self-contained console app that drives
`pty-req` + `env` + `shell` directly via `Microsoft.DevTunnels.Ssh` — the
same client library `egs-tool upload-file` uses — and pumps channel output
to its stdout, which a script can capture normally. Same server, scriptable
client.

The selector logic itself is also covered by unit tests
(`DefaultShellSelectorTests` + `KvpShellSelectorTests`, 13 tests total). The
e2e suite verifies the wire-level behavior on top.

## How the patch works

`Update-EgsService` (in `Helpers.ps1`):

1. `egs-tool upload-directory` pushes the `publish/` output to `C:\egs-staging`
   in the VM (works while service is running).
2. Uploads a `deploy.ps1` script alongside it.
3. Triggers a one-shot scheduled task that runs the deploy script as `SYSTEM`.
   The task is detached so the SSH session that triggered it can disconnect
   when the service stops (the service *is* the SSH server).
4. `Wait-Assert` polls `egs-tool get-status <vmId>` until `available` returns,
   then probes `ssh hostname` once to confirm end-to-end.

If a deploy fails, `C:\egs-staging\deploy.log` inside the VM has the timestamps
of each step.

## Provisioning suite

End-to-end tests for the embedded ProvisioningHostedService. Different shape
from the Shell suite:

1. Creates a base catlet (`provisioning-catlet.yaml`). The parent gene gives
   Windows + cloudbase-init + a pre-installed (but unstarted) egs-service.
   The cloud-config fodder in the catlet config IS the test input — the
   payload our agent is asked to process.
2. The catlet is NOT started yet. We mount its VHD on the host
   (`Mount-CatletVhd`).
3. `Update-EgsServiceBinariesOffline` overwrites the existing egs-service
   binaries under `<vol>:\Program Files\eryph\guest-services\bin\` with our
   locally-built publish output.
4. Same call disables cloudbase-init: renames its install dir to
   `.disabled-<ts>` AND sets the cloudbase-init service `Start=Disabled` in
   the offline `SYSTEM` hive.
5. Dismount, start the catlet.
6. On first boot, only `egs-service` runs (with our patched binaries). The
   embedded `ProvisioningHostedService` discovers the ConfigDrive datasource
   produced from the catlet's fodder, processes the cloud-config, runs the
   stages, and reports state via KVP. `SetHostnameModule` may trigger a
   reboot; `Wait-ForProvisioningComplete` polls through that.
7. Tests assert KVP reads `eryph.provisioning.state = completed`, the on-disk
   `state.json` includes the `Final` stage, the cloud-config outcomes are
   visible inside the guest (hostname set, user created, write_files
   markers, runcmd marker), and cloudbase-init never started.

```powershell
pwsh ./Run-ProvisioningE2ETests.ps1            # default winsrv2022
pwsh ./Run-ProvisioningE2ETests.ps1 -OSVersion winsrv2025
pwsh ./Run-ProvisioningE2ETests.ps1 -SkipBuild
```

**Requires Administrator** — `Mount-VHD` + offline `reg load` need elevated
rights. The Shell suite doesn't.

### Why VHD-mount + offline service registration (instead of post-boot patch)

The Shell suite patches AFTER first boot because its gene's first-boot fodder
*installs* `egs-service`. That's fine for testing shell behavior, but it
means cbi has already done first-boot provisioning by the time the patched
binary runs — we'd be testing a *second-boot* code path, not first-boot.

Our embedded `ProvisioningHostedService` lives or dies at first boot:
discovers a datasource, runs the stages, reports via KVP. Testing it
meaningfully requires the new binary to be the one Windows starts during its
very first SCM cycle. VHD-mount + offline service registration achieves
that without touching the host's running services or any post-OOBE state.

## Troubleshooting

- Set `$env:EGS_E2E_KEEP_VM=1` to leave the catlet running after the suite
  finishes (or fails). Useful for post-mortem inspection via Hyper-V Manager.
- Provisioning suite mount/dismount failures usually mean the catlet was
  still in `Saved` state from a previous run — `Get-VM -Id <vmid> | Remove-VM`
  manually or use `Remove-Catlet`. The suite checks `State -eq 'Off'` before
  mounting and aborts with a clear error otherwise.
- `reg load` failures: another `reg.exe` may have the hive loaded under a
  different mount point. List with `reg query HKLM | findstr Offline` and
  unload manually.
