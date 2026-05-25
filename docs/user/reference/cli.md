# `egs-service` CLI

Every subcommand exits 0 on success. `--state-dir <DIR>` overrides the state
root (default `%ProgramData%\eryph\provisioning`) and works on any subcommand —
useful for trying the agent in a sandbox.

## `run`

Runs the stages and exits.

```powershell
egs-service run
egs-service run --dry-run --user-data C:\Temp\sample.yaml
egs-service run --stage network
egs-service run --instance-id i-12345 --user-data C:\Temp\sample.yaml
```

| Flag | Description |
| --- | --- |
| `--dry-run` | Log what would happen; change nothing, and don't reboot. |
| `--stage <s>` | Run one stage only: `local`, `network`, `config`, `final`. |
| `--user-data <PATH>` | Use a local file instead of a discovered datasource. |
| `--instance-id <ID>` | Override the instance id (treated as a fresh instance). |
| `--state-dir <DIR>` | Override the state root. |

Exit codes: `0` success (including no datasource found, or a reboot triggered);
`1` a stage failed; `2` bad arguments (unknown `--stage`, missing `--user-data`).

## `status`

```powershell
egs-service status
egs-service status --json
egs-service status --wait
```

Reads the on-disk state, so it reflects what the next boot will see. `--wait`
polls every 2 seconds for up to 60 minutes until the Final stage has completed.
Exit codes: `0` success, `3` `--wait` timed out.

## `reset`

Clears semaphores and state, like `cloud-init clean`. See
[Reset and re-run](../howto/reset-and-rerun.md).

```powershell
egs-service reset
egs-service reset --keep-per-boot
egs-service reset --reset-once
egs-service reset --logs --scripts
```

| Flag | Description |
| --- | --- |
| `--logs` | Also delete per-script logs. |
| `--scripts` | Also delete staged user-data scripts. |
| `--reset-once` | Also clear per-once semaphores (kept by default). |
| `--keep-per-boot` | Keep per-boot semaphores (cleared by default). |
| `-y` / `--yes` | Accepted for script compatibility; reset never prompts. |

## `collect-logs`

```powershell
egs-service collect-logs C:\Temp\egs-bundle.zip
```

Zips the state file, the logs, the staged scripts, and a version file into the
given path, overwriting it. Missing inputs are skipped.

## `validate`

Runs the same parser and checks the agent uses at boot, without applying
anything.

```powershell
egs-service validate --user-data C:\Temp\sample.yaml
egs-service validate --user-data C:\Temp\sample.yaml --target windows
```

| Flag | Description |
| --- | --- |
| `--user-data <PATH>` | The cloud-config file to check (required). |
| `--target <TARGET>` | Portability check: `windows`, `linux`, or `all` (default). |

`--target windows` warns about each top-level key in the file that does nothing
on Windows (`apt`, `chef`, `phone_home`, …) — handy in CI to catch cross-cloud
configs drifting into Linux-only territory. `--target linux` flags the reverse
(today, `license`). `--target all` skips the portability check. Validation runs
either way.

Exit codes: `0` valid (portability warnings are informational); `1` rejected;
`2` not parseable, or an unknown `--target`.

## `version`

```powershell
egs-service version
```

Prints the agent name and version.

## Turning a capability off

To disable first-boot provisioning or the remote-access transport on a guest,
use the registry flags `ProvisioningEnabled` / `RemoteAccessEnabled` under
`HKLM\SOFTWARE\eryph\guest-services` — see
[Service control](settings.md#service-control-registry).
