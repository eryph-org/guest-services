# RFC 0003 — Module frequencies (per-instance / per-boot / per-once)

Status: Implemented

## Problem

Cloud-init modules and scripts declare a `frequency`: how often they run.
- `per-instance` — once per instance-id (default; the common case)
- `per-boot` — every boot
- `per-once` — exactly once, ever, regardless of instance-id

Pre-implementation we supported only `per-instance` (via `state.CompletedHandlers`), and the only way to force a re-run on the same instance was to delete `state.json` wholesale.

## What cloud-init does

Each module's metadata declares a frequency; state directory `/var/lib/cloud/instance/sem/<module>.<freq>` (per-instance), `/var/lib/cloud/sem/<module>.<freq>` (per-once / per-boot). The runner checks the semaphore before invoking the module.

## What cloudbase-init does

Implicit per-instance via plugin status keyed off instance-id. No `per-boot`. The PRE_NETWORKING stages re-run every boot — but that's accidental, not chosen.

## Decision

Implemented on the eryph provisioning agent in lockstep with [RFC 0010](0010-semaphore-design.md):

- Each module declares its frequency on the `[Stage]` attribute: `Frequency = ModuleFrequency.PerInstance | PerBoot | PerOnce`. The attribute defaults to `PerInstance` but we set it explicitly on every module so the choice is deliberate at module-write time.
- The `StageRunner` checks `ISemaphoreStore.ExistsAsync(module, frequency, instanceId)` before invoking `IModule.ApplyAsync`. Existing marker => skip and log at `Information` level.
- On `Completed` / `RebootRequested`, the runner writes the marker. The `RebootRequested` path writes the marker BEFORE returning so the post-reboot pass does not loop.
- On `Failed`, no marker is written — the module re-runs on the next pass.
- v1 modules are all `PerInstance` (every existing module is `cc_*` style host configuration).

## Boot session detection

Per-boot semaphores must be cleared at the start of every new boot. We compare the current boot identifier against a marker file (`%ProgramData%\eryph\provisioning\last-seen-boot.json`); on mismatch the per-boot directory is wiped.

Two options were considered:

(a) **`Win32_OperatingSystem.LastBootUpTime` via CIM** — chosen. A documented Win32 property surfaced through `Microsoft.Management.Infrastructure` (already a dependency for `Win32_ComputerSystem.Rename`). Updated on every cold boot and on resume-from-hibernate.

(b) `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\BootId` (or `HKLM\SYSTEM\...\Memory Management\BootId`) — rejected. The incrementing-integer behaviour is not documented, the semantics around hibernate / Fast Startup are unclear, and there's no public guarantee that the value is stable across in-place upgrades.

If the CIM read fails (e.g. WMI service stopped), the detector defaults to "new boot": per-boot modules run again. That's the safer error mode — re-running a per-boot module is idempotent by contract, while suppressing one that should have run is not.

The detector is implemented as `BootSessionDetector` over an `IBootClock` seam (`Win32BootClock` for production) so tests can drive the timeline deterministically.

## Open questions (deferred)

- How does `per-once` interact with eryph image baking (templates)? If a per-once module ran at template build time, does the per-once semaphore travel with the image? Today: yes, it lives under `%ProgramData%` which is captured by templating. Operators that want a per-once module to re-run on every fresh deploy can pass `--reset-once` to the agent on first boot of the gold image.
- Cleanup of `per-boot` semaphores — currently they are cleared every boot but accumulate within a boot. Sufficient for v1; a TTL-based purge can be added if module count grows.
- Does `power_state_change`-induced reboot count as "next boot" for `per-boot`? Yes — `LastBootUpTime` ticks on every cold boot and resume-from-hibernate, regardless of who initiated it.
