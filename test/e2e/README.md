# Configurable-shell e2e tests

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

Three active tests under `set-shell tool command`:

- Writes shell + shell-args to the External KVP pool (use `--arguments=-...`
  syntax — Spectre.Console.Cli treats space-separated `-`-prefixed values as
  new flags)
- Writes only shell when no arguments are passed
- `--reset` clears both keys

Five `shell selection` tests are present but **skipped** (`Context ... -Skip`).
They need to drive an interactive shell session (`pty-req + env + shell` over
SSH) to verify that the configured executable was actually spawned. Win32-
OpenSSH's `ssh.exe` with `-tt` and redirected stdin silently refuses to send
`pty-req` — the channel opens and closes without ever invoking `ShellService`.
Properly testing this end-to-end requires a custom probe built on
`Microsoft.DevTunnels.Ssh` (e.g. a small `EgsShellProbe` console app). The
selector logic itself is covered exhaustively by the unit-test suites:

- `Eryph.GuestServices.DevTunnels.Ssh.Extensions.Tests/DefaultShellSelectorTests.cs`
  — env-var honoring + platform default
- `Eryph.GuestServices.Service.Tests/KvpShellSelectorTests.cs` — full chain
  (KVP > env > default), KVP-read failure handling, blank-value fall-through

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
