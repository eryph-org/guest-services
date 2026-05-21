# RFC 0004 — Datasource readiness: probe timeout, retry backoff

Status: Draft

## Problem

The `DataSourceProbeResult.WaitForReady` state asks the locator to retry the same datasource after a backoff. Concrete questions:

- Per-probe timeout (how long can one `ProbeAsync` block before being aborted)?
- Per-datasource overall timeout (how long total can we keep retrying)?
- Backoff between retries (constant, linear, exponential)?
- Behavior on overall-timeout: log and try next datasource, or fail the whole provisioning?

## What cloud-init does

Cloud-init's `wait_for_metadata_service` per-datasource has hard-coded timeouts (varies per datasource — EC2 IMDS uses ~120s, Azure ~30 min from wireserver). Backoff is exponential.

## What cloudbase-init does

Each metadata service has its own retry; no central policy. Plugins fail or succeed; no `WaitForReady` concept.

## Eryph context

- ConfigDrive / NoCloud / KVP — instant probe. `WaitForReady` should never fire for these.
- Azure — waits for `C:\AzureData\CustomData.bin` to be written by PA. Usually < 5 min.
- EC2 — waits for EC2Launch's marker. Timing TBD.

## Tentative direction

- Per-probe timeout: 30 s (cap a single `ProbeAsync` invocation; protects against a hung registry/WMI call).
- Per-datasource overall timeout: 10 min (default; configurable via `egs-provisioning.json`).
- Backoff: each datasource specifies its own backoff in `WaitForReady(Reason, Backoff)`. The locator just sleeps. Datasources pick reasonable values (Azure: 5 s, growing to 30 s after the first minute).
- On overall-timeout: log Error, return `Failed`, try next datasource. Don't abort provisioning yet — fall through gives the user a chance to recover via cloud-config on a lower-priority datasource.

## Open questions

- Per-datasource overrides via the settings file (`egs-provisioning.json`): `dataSources.azure.timeoutMinutes`?
- Should the locator emit `ReportingEvent.Progress` events while waiting, so the host sees "still waiting for Azure PA"?
