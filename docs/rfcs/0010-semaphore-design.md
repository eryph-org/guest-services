# RFC 0010 — Semaphore design: single JSON vs per-module files

Status: Draft

## Problem

Cloud-init tracks per-module-per-instance-or-per-boot completion via filesystem semaphores:
- `/var/lib/cloud/instance/sem/<module>.<freq>` (per-instance)
- `/var/lib/cloud/sem/<module>.<freq>` (per-once)

We currently use a single `state.json` with a `CompletedHandlers` HashSet (treated as per-instance only). Should we adopt cloud-init's per-module file design?

## Pros of per-module files

- One file per module = atomic write per completion; no big-blob race conditions.
- Different frequencies are physically separated (no need to encode `.per-boot` in the hashset key).
- Easier to "force re-run module X" by deleting one file; harder to fat-finger.
- Cloud-init mental model — operators who know cloud-init don't have to learn a new state format.

## Cons of per-module files

- More inodes, more file I/O for the StageRunner.
- Reading "which modules have completed" requires a directory listing instead of one JSON read.
- Backup/restore of state is multiple files instead of one.

## Tentative direction

**Stick with single JSON for v1. Adopt per-module files for v2 if RFC 0003 (frequencies) lands.** Reasons:
- v1 has only `per-instance` — a single JSON works.
- v2 adding `per-boot` means we need to separate frequency-tracking from per-instance state — cloud-init's per-file approach scales cleanly.
- Migration path is a one-time conversion (read `state.json`, write per-module files); no compat headache.

## Open questions

- Naming convention: `%ProgramData%\eryph\provisioning\sem\<instance-id>\<module>.<freq>`? Mirror cloud-init layout?
- File contents: just touchfile (existence = completed), or include timestamp / exit code / outcome?
- Cleanup: per-instance semaphores deleted on instance-id change. Per-boot semaphores deleted at start of every boot.
- `state.json` keeps non-semaphore data (current run start time, reboot count, last reported state).
