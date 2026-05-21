# RFC 0005 — Datasource cleanup hook timing

Status: Draft

## Problem

`IDataSource.OnCompletedAsync` lets a datasource clean up after successful provisioning (e.g., Azure datasource deletes `C:\AzureData\CustomData.bin`). When does this fire, and what counts as "successful"?

## What cloudbase-init does (reference)

`provisioning_completed()` on the metadata service fires only when every plugin finished AND no plugin requested a reboot in the most recent run (`init.py:228–232`). The eryph Azure patch uses this to remove `CustomData.bin`.

## What cloud-init does

Cloud-init has `DataSource.deactivate()` (called when instance state changes) and a per-datasource `clean()` hook. The relevant moment for cleanup is at end of `Final` stage, after all modules ran.

## Eryph options

1. **Only on full success** — every stage completed, no reboot pending. Mirrors cbi.
2. **On partial success** — fire after every stage, with the stage index passed in. Datasources can choose what to clean per-stage.
3. **On any terminal state** — success or failure. Datasources clean up regardless of outcome. Risky: a transient failure might delete payload that should be retried next boot.

## Tentative direction

**(1) Only on full success.** Lowest surprise; matches cbi. Datasources that want richer signaling can read state via reporting events (RFC 0006).

## Open questions

- Reboot-and-continue: `OnCompletedAsync` should NOT fire on `RebootRequested` — the run isn't done yet. Confirm by walking the StageRunner flow.
- What if `OnCompletedAsync` itself throws? Log Error, do not fail the run (provisioning already succeeded by the time we got here).
