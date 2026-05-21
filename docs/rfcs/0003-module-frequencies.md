# RFC 0003 — Module frequencies (per-instance / per-boot / per-once)

Status: Draft

## Problem

Cloud-init modules and scripts declare a `frequency`: how often they run.
- `per-instance` — once per instance-id (default; the common case)
- `per-boot` — every boot
- `per-once` — exactly once, ever, regardless of instance-id

We currently only support `per-instance` (via `state.CompletedHandlers`). What about the others?

## What cloud-init does

Each module's metadata declares a frequency; state directory `/var/lib/cloud/instance/sem/<module>.<freq>` (per-instance), `/var/lib/cloud/sem/<module>.<freq>` (per-once). The runner checks the semaphore before invoking the module.

## What cloudbase-init does

Implicit per-instance via plugin status keyed off instance-id. No `per-boot`. The PRE_NETWORKING stages re-run every boot — but that's accidental, not chosen.

## Eryph context

- v1 modules are all `per-instance` (configuration that survives reboots).
- `per-boot` is useful for things like reporting current state, refreshing dynamic config, or running `#cloud-boothook`-style scripts.
- `per-once` is exotic — first-ever-boot system setup that never repeats even on redeploy.

## Tentative direction

- v1: `per-instance` only (current behavior).
- v2: add `per-boot` (a `RuncmdPerBootHandler` or similar) — semaphore stored separately so the per-instance counter doesn't reset every boot.
- `per-once` deferred to a real use case.

## Open questions

- How does `per-once` interact with eryph image baking (templates)? If a per-once module ran at template build time, does the per-once semaphore travel with the image?
- Cleanup of `per-boot` semaphores — never, or after N days?
- Does `power_state_change`-induced reboot count as "next boot" for `per-boot`? Cloud-init: yes.
