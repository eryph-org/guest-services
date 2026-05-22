# RFC 0010 — Semaphore design: per-module files mirroring cloud-init

Status: Implemented

## Problem

Cloud-init tracks per-module-per-instance-or-per-boot completion via filesystem semaphores:
- `/var/lib/cloud/instance/sem/<module>.<freq>` (per-instance)
- `/var/lib/cloud/sem/<module>.<freq>` (per-boot, per-once)

Pre-implementation we used a single `state.json` with a `CompletedHandlers` HashSet (treated as per-instance only). With [RFC 0003](0003-module-frequencies.md) adding `per-boot` and `per-once`, the single-blob design no longer expresses the required scopes cleanly.

## Decision

Adopted cloud-init's per-module file design, adapted for Windows paths:

```
%ProgramData%\eryph\provisioning\
├── instance\<instance-id>\sem\<module>.per-instance
├── sem\<module>.per-boot
├── sem\<module>.per-once
├── last-seen-boot.json            -- BootSessionDetector marker
└── state.json                     -- legacy CompletedHandlers + bookkeeping
```

The `<module>` segment is the fully-qualified .NET type name (e.g. `Eryph.GuestServices.Provisioning.Modules.UsersGroupsModule`), matching what `state.CompletedHandlers` used to carry. The `<instance-id>` segment is sanitised against `Path.GetInvalidFileNameChars()` so a CLI override datasource cannot escape the root.

Marker file content is a JSON line `{"timestamp": "...", "instanceId": "...", "outcome": "..."}` so operators can debug what happened. **Existence** is what gates execution; the contents are diagnostic only. Writes are atomic (temp file + `File.Move(overwrite: true)`).

## Pros of per-module files (realised)

- One file per module = atomic write per completion; no big-blob race conditions.
- Different frequencies are physically separated (no need to encode `.per-boot` in the hashset key).
- Easier to "force re-run module X" by deleting one file; harder to fat-finger.
- Cloud-init mental model — operators who know cloud-init don't have to learn a new state format.

## Cons of per-module files (mitigated)

- More inodes, more file I/O for the StageRunner. Acceptable: <20 modules per stage and a single check per module per stage run.
- Reading "which modules have completed" requires a directory listing. Mitigated by `ISemaphoreStore.ListPerInstanceAsync`, used by the `state.json` migration path.
- Backup/restore is multiple files. Acceptable: the directory under `%ProgramData%\eryph\provisioning` is self-contained.

## Migration from `state.json.CompletedHandlers`

On startup, when an existing `state.json` carries `CompletedHandlers` from the pre-semaphore release, the `StageRunner` promotes each entry to a per-instance semaphore (`outcome: "migrated-from-state.json"`). Subsequent runs gate off the semaphore. `CompletedHandlers` is kept on `ProvisioningState` for one release with an XML-doc `<summary>` marking it deprecated and pointing at `ISemaphoreStore`.

## Reset semantics

`egs-tool reset` mirrors `cloud-init clean`:

| Flag                    | Per-instance | Per-boot | Per-once |
|-------------------------|--------------|----------|----------|
| (no flag, default)      | clear        | clear    | keep     |
| `--keep-per-boot`       | clear        | keep     | keep     |
| `--reset-once`          | clear        | clear    | clear    |
| `--reset-once --keep-per-boot` | clear | keep    | clear    |

`--logs` and `--scripts` continue to clear the respective directories on demand. The boot session marker (`last-seen-boot.json`) is always cleared so the next agent run treats itself as a new boot.

## Open questions (deferred)

- Should we expose `egs-tool semaphore list` and `egs-tool semaphore clear <module>` for operator diagnostics? Today operators inspect or delete files by hand; that's fine while the count stays small.
- Per-vendor-data semaphores (cloud-init has a separate `vendordata.txt` digest semaphore). Will revisit when vendor-data modules ship.
