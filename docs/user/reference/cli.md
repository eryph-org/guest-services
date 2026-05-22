# `egs-service` CLI

Every subcommand exits 0 on success. Common flags:

- `--state-dir <DIR>` — override the state root (default
  `%ProgramData%\eryph\provisioning`). Lets you exercise the agent in
  a sandbox.

## `run` — execute the stages

Runs the configured stages synchronously and exits.

```powershell
egs-service run
egs-service run --dry-run --user-data C:\Temp\sample.yaml
egs-service run --stage network
egs-service run --instance-id i-12345 --user-data C:\Temp\sample.yaml
```

| Flag | Description |
| --- | --- |
| `--dry-run` | Log intended actions; do not mutate the guest. Reboots are also suppressed. |
| `--stage <s>` | Run a single stage only: `local`, `network`, `config`, `final`. |
| `--user-data <PATH>` | Override the datasource with the bytes of a local file. |
| `--instance-id <ID>` | Override the instance id. Implies fresh-instance treatment. |
| `--state-dir <DIR>` | Override the state root. |

Exit codes:
- `0` — Success or `NoDataSource` or `RebootRequested` (with reboot triggered or dry-run).
- `1` — Failed (any stage threw).
- `2` — Bad CLI args (e.g. unknown `--stage`, missing `--user-data` file).

## `status` — inspect the current state

```powershell
egs-service status
egs-service status --json
egs-service status --wait
```

Reads `state.json` from disk (never an in-memory copy) so the result
reflects what the next agent boot will see. `--wait` polls every 2s
for up to 60 minutes until the `Final` stage is in
`completedStages`.

Exit codes: 0 on success, 3 on `--wait` timeout.

## `reset` — clear semaphores and state

Mirrors `cloud-init clean`. See
[Reset and re-run](../howto/reset-and-rerun.md) for the matrix.

```powershell
egs-service reset
egs-service reset --keep-per-boot
egs-service reset --reset-once
egs-service reset --logs --scripts
```

| Flag | Description |
| --- | --- |
| `--logs` | Also delete `logs\` (per-script logs). |
| `--scripts` | Also delete staged user-data scripts. |
| `--reset-once` | Also clear per-once semaphores. Default behavior keeps them. |
| `--keep-per-boot` | Keep per-boot semaphores. Default behavior clears them. |
| `-y` / `--yes` | No-op (reset never prompts). Kept for script compatibility. |

## `collect-logs` — bundle the state for support

```powershell
egs-service collect-logs C:\Temp\egs-bundle.zip
```

Zips `state.json`, `logs\`, `scripts\`, and a `version.txt`. Overwrites
the output. Missing inputs are skipped silently.

## `validate` — sanity-check a cloud-config

Runs the same parser and validators the agent uses at run-time, without
applying anything.

```powershell
egs-service validate --user-data C:\Temp\sample.yaml
```

Exit codes: 0 valid, 1 validation rejected, 2 not parseable.

## `version` — print the agent version

```powershell
egs-service version
```

Prints the entry-assembly name + version + the
`AssemblyInformationalVersionAttribute` informational string.
