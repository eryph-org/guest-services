# RFC 0013 — Boothook execution

Status: Draft

## Problem

`#cloud-boothook` user-data is a script type cloud-init runs **very early** — before most modules — on every boot. Use cases: configure mount points before disk_setup, install missing dependencies, fix the boot environment.

The user-data pipeline (v1) **captures** `BoothookPayload`s but doesn't execute them. This RFC decides when and how.

## What cloud-init does

Boothooks run in the Network stage, BEFORE `cloud_init_modules`. Execution is per-boot (the script may guard itself with `INSTANCE_ID` env var to act per-instance). Output goes to `/var/log/cloud-init-output.log`.

## What cloudbase-init does

No boothook support.

## Eryph context

- No eryph use case in current genes.
- The slot exists in `ResolvedUserData.Boothooks` for forward-compat.

## Tentative direction

**v2**: add `BoothookHandler` in `Stage.Local` (runs even before Network's first module). Execution is `per-boot` (RFC 0003).

For v1: captured payloads are logged at Information level ("found N boothook(s); execution deferred") and discarded.

## Open questions

- Boothooks on Windows: `#!` shebang doesn't work natively. Detect `#ps1` shebang and run as PowerShell, or strictly require boothooks to start with `#cloud-boothook` followed by PowerShell script body?
- Per-boot vs per-instance: cloud-init has `INSTANCE_ID` env var so scripts can self-gate. We should pass the same.
