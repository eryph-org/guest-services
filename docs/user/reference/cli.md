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
egs-service validate --user-data C:\Temp\sample.yaml --target windows
egs-service validate --user-data C:\Temp\sample.yaml --target linux
```

| Flag | Description |
| --- | --- |
| `--user-data <PATH>` | Path to the cloud-config user-data file (required). |
| `--target <TARGET>` | Platform portability check: `windows`, `linux`, or `all` (default). |

`--target windows` walks the source-generated platform inventory and
emits a Warning for every top-level key present in the YAML that has
no Windows behaviour (e.g. `apt`, `chef`, `phone_home`). Useful in CI
to flag cross-cloud cloud-config that drifts into Linux-only territory.

`--target linux` mirrors the check from the other direction — flags
Windows-only keys (today: `license`).

`--target all` (the default) is the lenient form — no portability
warnings. Validation always runs regardless.

Exit codes: 0 valid (portability warnings are informational), 1
validation rejected, 2 not parseable or unknown `--target` value.

## `version` — print the agent version

```powershell
egs-service version
```

Prints the entry-assembly name + version + the
`AssemblyInformationalVersionAttribute` informational string.

## Disabling a capability

To turn off first-boot provisioning or the remote-access SSH transport on a
guest, use the opt-out registry flags `ProvisioningEnabled` /
`RemoteAccessEnabled` under `HKLM\SOFTWARE\eryph\guest-services`. See
[Service control (registry)](settings.md#service-control-registry).
