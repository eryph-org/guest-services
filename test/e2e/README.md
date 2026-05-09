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

Eight tests in two contexts:

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
