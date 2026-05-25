# Architecture

A run has four parts — the datasource locator, the user-data pipeline, the stage
runner, and reporting — all in one process, in this order:

```
+-------------------+
|  Datasource probe |   find one source, get the instance id + bytes
+--------+----------+
         |
         v
+-------------------+
|  User-data parse  |   bytes -> cloud-config + scripts
+--------+----------+
         |
         v
+-------------------+
| Stage runner      |   Local -> Network -> Config -> Final
|                   |     per module: skip if its semaphore is set, else run
+--------+----------+
         |
         v
+-------------------+
| Cleanup hook      |   e.g. delete CustomData.bin on Azure
+-------------------+

Reporting (log + Hyper-V KVP) runs throughout.
```

## Datasource locator

The locator probes the registered datasources in priority order. The first one
with data wins; a source that isn't ready yet is retried with backoff, and the
retries across all not-yet-ready sources share one budget. If every source has
been ruled out or the budget runs out, the run ends cleanly with no datasource —
the "this isn't a provisioning environment" case, where a plain Hyper-V VM with
no fodder lands. See [Datasources](../reference/datasources.md).

## User-data pipeline

The pipeline turns the raw bytes into a merged cloud-config plus a list of
scripts and boothooks. It decompresses gzip, reads the marker on the first line,
and hands the payload to the matching handler. Handlers can recurse — a multipart
opens its parts, an `#include` fetches more payloads — and each one either merges
a cloud-config fragment or appends a script. Recursion is capped by
`userData.maxRecursionDepth` (default 10), and already-fetched `#include` URLs
are skipped to avoid loops.

## Stage runner

The stage runner is the orchestrator. It picks the datasource, clears per-boot
semaphores when it sees a new boot, loads the instance's state, and works through
the stages — running each module in order, skipping any whose semaphore is set.
When a module asks for a reboot it records that module as done and the guest
reboots, resuming on the next boot. After the Final stage it runs the datasource
cleanup hook.

## Reporting

Provisioning events are sent to every reporting handler as the run proceeds. Two
ship today: one logs through the standard logging pipeline, and one writes
provisioning state and per-stage progress into the Hyper-V KVP channel when the
guest runs on Hyper-V — that's what `egs-tool get-status` reads. Webhook and
cloud-native reporting backends aren't shipped.

## State

`%ProgramData%\eryph\provisioning\state.json` records the instance id, start and
update times, reboot count, and which stages have completed. Module skipping is
driven by the per-module semaphore files, not this file. `egs-service status`
reads the file from disk, so what it shows is what the next boot will see.
