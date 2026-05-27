# e2e tests against a real eryph catlet

Two suites:

- `Shell.E2E.Tests.ps1` — configurable-shell tests; spins up a winsrv VM with
  the dbosoft/guest-services gene, post-boot patches the gene-installed
  egs-service, and exercises the SHELL selection chain (KVP > SSH env > defaults).
- `Provisioning.E2E.Tests.ps1` — embedded-provisioning tests; creates a BASE
  catlet (no fodder), mounts its VHD BEFORE first start, bakes our egs-service
  binary in, disables cloudbase-init, and asserts the embedded provisioning
  lifecycle runs cleanly at first boot via KVP and state.json.
- `OpenStack.E2E.Tests.ps1` — OpenStack metadata-service (HTTP) datasource
  tests; deploys an Ubuntu simulator (`egs-openstack-sim`) pinned to
  169.254.169.254 serving the captured config-2 fixture, prepares a Windows
  guest offline (sets the SMBIOS chassis asset tag + pins the datasource list),
  and asserts it provisions from the HTTP metadata service. REQUIRES ADMIN.

The two suites have different goals so they're separate runners:

```powershell
pwsh ./Run-E2ETests.ps1                       # Shell suite, winsrv2022
pwsh ./Run-E2ETests.ps1 -OSVersion winsrv2025
pwsh ./Run-E2ETests.ps1 -SkipBuild            # reuse existing publish output
pwsh ./Run-ProvisioningE2ETests.ps1           # Provisioning suite — REQUIRES ADMIN
pwsh ./Run-OpenStackE2ETests.ps1              # OpenStack HTTP datasource — REQUIRES ADMIN
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
4. Same call disables cloudbase-init at three levels:
   - Renames its install dir to `.disabled-<ts>`.
   - Sets the cloudbase-init service `Start=Disabled` in the offline `SYSTEM`
     hive (so the SCM doesn't try to start a service whose binary just moved).
   - Patches every `unattend.xml` it finds (`Windows\System32\Sysprep\`,
     `Windows\Panther\`, `Windows\Panther\unattend\`, root) to replace any
     `RunSynchronousCommand` referencing cloudbase-init with `cmd.exe /c "exit 0"`
     and `WillReboot=Never`. Without this last step, sysprep's OOBE specialize
     phase runs cbi.exe at the renamed path, gets a non-zero exit, and halts
     before egs-service ever starts.
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

## OpenStack suite

End-to-end test for the OpenStack metadata-service (HTTP) datasource
(`OpenStackMetadataDataSource`) against **real captured nova metadata**.

> **Status: "technically working", NOT production-ready.** This exercises the
> datasource against captured fixtures + a faithful-shape simulator on
> eryph/Hyper-V. It has never run against a real OpenStack deployment, where
> link-local reachability and dynamic IMDS behavior differ. See the datasource's
> `DESIGN.md` ("Maturity") for the full caveat list.

Topology — two overlay networks from `openstack-sim-network.yaml`; the guest
sits on `default`, the simulator on `metadata`. eryph's virtual router connects
them, but `169.254.169.254` is link-local so a Windows guest treats it as on-link
and never routes it — the harness installs an onstart task in the guest that adds
the explicit `/32`-via-gateway route (see step 4). (On *real* OpenStack this is
handled by the neutron metadata agent / DHCP option 121, not a manual route.)

1. Applies the network config: `default` (guest) + `metadata` (single-IP pool
   pinning the simulator to `169.254.169.254`).
2. Deploys the **simulator** catlet (Ubuntu). The harness uploads
   `egs-openstack-sim` + the captured fixture tree
   (`test/fixtures/configdrive-openstack`) via egs and runs the sim as a systemd
   service on port 80. `egs-openstack-sim` serves the OpenStack contract:
   `GET /openstack` → version listing, then `openstack/<version>/…` files.
3. Creates the **guest** catlet (Windows) and prepares it offline before first
   boot: `Update-EgsServiceBinariesOffline` (bake in egs-service + disable cbi),
   `Set-CatletChassisAssetTag` → `"OpenStack Nova"` (Hyper-V can't set
   system-product-name, but the asset tag trips the same `ds_detect` gate), and
   `Set-OfflineProvisioningSettings` to pin `dataSources.dataSourceList` to
   `["OpenStack"]` (so the locator probes ONLY the metadata service — eryph's own
   config-2 drive, ConfigDrive priority 40, would otherwise win over OpenStack 50).
4. Starts the guest, then `Install-GuestMetadataRoute` registers an onstart
   scheduled task (re-runs across the SetHostnameModule reboot) that adds the
   active `169.254.169.254/32` route. egs-service comes up independently and its
   OpenStack datasource WaitForReady-loops until the route lands.
5. egs-service detects OpenStack via the asset tag, fetches `meta_data.json` +
   `user_data` over HTTP from `169.254.169.254`, and provisions.

Assertions prove the data came from the HTTP service: KVP
`eryph.provisioning.instance` equals the simulator's sanitized uuid
(`facade00-…`, which eryph's own drive would never carry), `state.json` reached
`Final`, the `user_data` `write_files` marker
(`C:\eryph-openstack-e2e\hello.txt = from-userdata`) was written, and — the
discriminating "usable VM" check — the `user_data` `users:` block provisioned a
login account (`osadmin`, Administrator, enabled, password authenticates via
`ValidateCredentials`). `vendor_data` is covered by unit tests, not here.

```powershell
pwsh ./Run-OpenStackE2ETests.ps1               # default winsrv2022
pwsh ./Run-OpenStackE2ETests.ps1 -OSVersion winsrv2025
pwsh ./Run-OpenStackE2ETests.ps1 -SkipBuild
```

**Requires Administrator** (Mount-VHD, offline reg load, Hyper-V WMI for the
chassis asset tag) and an Ubuntu 22.04 starter gene in addition to the Windows
parent. The harness publishes `egs-openstack-sim` for linux-x64 self-contained.

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
