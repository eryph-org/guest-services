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

Eight tests in two groups:

- `set-shell tool command` — writes shell+shell-args, writes shell only,
  `--reset` clears both keys.
- `shell selection` — default spawns Windows PowerShell, KVP override
  spawns `pwsh`, SSH-sent `SHELL` env var spawns `pwsh`, KVP wins over SSH
  env, `--reset` returns to default.

## Troubleshooting

- Set `$env:EGS_E2E_KEEP_VM=1` to leave the catlet running after the suite
  finishes (or fails). Useful for post-mortem inspection via Hyper-V Manager.
- If the service patch step times out, `C:\egs-staging\deploy.log` inside the
  VM has timestamps of each deploy step.
