# How to reset and re-run provisioning

`egs-service reset` clears the agent's state so the next `run` treats
the guest as a fresh instance. It mirrors `cloud-init clean`.

## What state looks like

Three kinds of semaphores live under
`%ProgramData%\eryph\provisioning`:

```
instance\<instance-id>\sem\<module>.per-instance    # per-instance markers
sem\<module>.per-boot                               # per-boot markers
sem\<module>.per-once                               # per-once markers
state.json                                          # run bookkeeping
last-seen-boot.json                                 # boot session marker
```

A module that has a matching semaphore is skipped on the next run. See
[Frequencies reference](../reference/frequencies.md) for what each scope
means and [RFC 0010](../../rfcs/0010-semaphore-design.md) for the design.

## What `reset` does by default

| Item | Default | `--reset-once` | `--keep-per-boot` |
| --- | --- | --- | --- |
| Per-instance semaphores + `state.json` | cleared | cleared | cleared |
| Per-boot semaphores | cleared | cleared | kept |
| Per-once semaphores | kept | cleared | kept |
| `last-seen-boot.json` | cleared | cleared | cleared |
| `logs\` | kept | kept | kept (use `--logs`) |
| `scripts\` | kept | kept | kept (use `--scripts`) |

`per-once` semaphores intentionally survive `reset` — they encode
"this should happen exactly once for the lifetime of the guest" and
that lifetime should not be wiped by a routine reset. Pass
`--reset-once` to force them clear.

## Recipes

### Full re-run on the same machine
```powershell
egs-service reset
egs-service run
```

### Re-run only the Final stage
```powershell
egs-service reset
egs-service run --stage final
```

### Force a per-once module to re-run once
```powershell
egs-service reset --reset-once
egs-service run
```

### Wipe everything including logs and staged scripts
```powershell
egs-service reset --reset-once --logs --scripts
egs-service run
```

### Reset against a custom state dir (test / CI)
```powershell
egs-service reset --state-dir C:\Temp\state
egs-service run --state-dir C:\Temp\state --user-data sample.yaml
```

## What `reset` does **not** do

- It does not roll back OS state. If a user account was created, the
  account stays. If `write_files` wrote a file, the file stays.
- It does not delete the cloud-init datasource. The cidata / config-2
  ISO is owned by the host; the agent never ejects it on its own.

## When `state.json` is corrupted

If you can't even read `state.json`, delete it manually:

```powershell
Remove-Item C:\ProgramData\eryph\provisioning\state.json
egs-service run
```

The agent treats a missing state file the same as a fresh instance.
