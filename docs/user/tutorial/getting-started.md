# Getting started

A 5-minute introduction to the provisioning agent. You'll run it once
against a local cloud-config file, in dry-run mode, and watch it walk
through the stages.

## Prerequisites

- Windows 10/11 or Windows Server 2016+.
- `egs-service.exe` installed (see the [repo README](../../../README.md)
  for installation methods).
- An elevated PowerShell prompt — the agent reads `%ProgramData%` and
  the dry-run still mutates state files.

## Step 1 — write a sample cloud-config

Save the following as `C:\Temp\sample.yaml`:

```yaml
#cloud-config
hostname: demo-guest
users:
  - name: alice
    plain_text_passwd: ChangeMe!42
    groups: [Administrators]
write_files:
  - path: C:\demo\hello.txt
    content: "hello from cloud-config"
runcmd:
  - powershell.exe -NoProfile -Command "Write-Host 'runcmd ran'"
```

## Step 2 — dry-run

```powershell
egs-service run --dry-run --user-data C:\Temp\sample.yaml
```

You should see, in order:

- An override-datasource probe (no real datasource is consulted because
  `--user-data` short-circuits discovery with a synthetic one).
- The four stages — `Local`, `Network`, `Config`, `Final` — log a header
  each, listing the modules they will run.
- A green "DRY-RUN: …" line for each action a module would take.
- A final `Provisioning completed.` line.

In dry-run no system state is changed: no user `alice` is created, no
file appears under `C:\demo`, no commands run. The state files under
`%ProgramData%\eryph\provisioning` *are* updated so the next run sees
this instance as already provisioned.

## Step 3 — inspect state

```powershell
egs-service status
```

Reads `state.json` and prints a small table — instance id, completed
stages, completed modules. Pass `--json` for raw output (handy in CI).

## Step 4 — reset and run again

To re-run on the same machine, clear the per-instance state:

```powershell
egs-service reset
egs-service run --dry-run --user-data C:\Temp\sample.yaml
```

`reset` mirrors `cloud-init clean`: per-instance and per-boot semaphores
go away, per-once survives unless you add `--reset-once`. See
[Reset and re-run](../howto/reset-and-rerun.md) for the full matrix.

## What's next

- [Your first catlet with cloud-config](first-catlet-with-cloud-config.md) — drop the `--dry-run` and ship a real cloud-config to a real eryph catlet.
- [Write a cloud-config](../howto/write-a-cloud-config.md) — the schema fields you can put in cloud-config.
- [Reference: CLI](../reference/cli.md) — every flag on `run`, `reset`, `status`, `collect-logs`, `validate`, `version`.
