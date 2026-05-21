# RFC 0007 — `scripts/per-*` semantics edge cases

Status: Draft

## Problem

Cloud-init's `scripts/per-instance`, `scripts/per-boot`, `scripts/per-once` directories execute scripts at appropriate frequencies. Several edge cases need explicit policy:

- Execution order within a directory (alphabetical? declaration order from user-data?)
- Script that exits non-zero: continue or abort the stage?
- Script that requests reboot via exit-1003: handled like a module reboot request?
- Permissions: do we chmod +x equivalent on Windows (set NTFS execute ACL)?
- Stdout/stderr capture: log? send to reporting events?

## What cloud-init does

- Alphabetical execution order.
- Non-zero exit: log Error, continue to next script (unless `power_state` or `runcmd` was the failure — those propagate).
- No reboot-and-continue: cloud-init isn't designed for that pattern. Script that wants reboot uses `power_state_change` module instead.
- Permissions: Linux chmod 755 on the file before execution.
- Stdout/stderr: redirected to `/var/log/cloud-init-output.log`.

## What cloudbase-init does

Runs scripts in declaration order. Exit code 1003 = reboot-and-continue (the convention we already adopted for `RuncmdModule`). Stdout/stderr logged via the cloudbase-init log.

## Tentative direction

- Order: **declaration order from user-data**, then alphabetical for anything written to the directory directly. Eryph genes emit scripts in a specific order and we shouldn't reshuffle.
- Non-zero exit: log Error, continue. Matches both cbi and cloud-init.
- Exit 1003 → `RebootRequested` outcome. Mid-stage abort; resume on next boot via the standard reboot-and-continue mechanism. (Cloud-init doesn't do this; we do because cbi-compat matters for eryph genes.)
- Permissions: skip (Windows doesn't care about execute bits).
- Stdout/stderr: capture, emit as `ReportingEvent.Progress` (one per script), and write to a per-script log file under `%ProgramData%\eryph\provisioning\logs\`.

## Open questions

- Cloud-init's "boothook" scripts run BEFORE `cloud-init-network.service` (very early). Our model runs everything after Network stage — does that break any eryph use case? (Probably no — `#cloud-boothook` is exotic.)
- Should script execution timeout be configurable per script, or one global timeout from `egs-provisioning.json`?
- What happens if `runcmd` (staged in Config) writes a script that the user-data ALSO supplied as `#!/...`? Two files in `scripts/per-instance/`. Define a name-collision policy.
