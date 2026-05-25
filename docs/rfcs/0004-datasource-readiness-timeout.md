# RFC 0004 — Datasource readiness: probe timeout, retry backoff

Status: Implemented

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

## Design (implemented)

The locator runs a single interleaved retry loop with a shared wall-clock budget. It diverges from cloud-init in two deliberate ways: the budget is global (not per-source), and sources that report `WaitForReady` do not block the discovery of lower-priority sources — see `DataSourceLocator.LocateAsync` for the loop.

### Backoff schedule

Per-source exponential growth with operator-tunable bounds. The datasource's own `WaitForReady.Backoff` value is a hint (floor); the locator clamps to `[MinBackoff, MaxBackoff]` and doubles the previous delay after each retry:

| Retry | Wait (default bounds 1s–60s) |
| ----- | ---------------------------- |
| 1     | 1s (or datasource hint)      |
| 2     | 2s                           |
| 3     | 4s                           |
| 4     | 8s                           |
| 5     | 16s                          |
| 6     | 32s                          |
| 7+    | 60s (capped at `MaxBackoff`) |

When a datasource hints a backoff larger than the doubled value, the hint wins (so a datasource that knows it needs ≥30s before retry is honoured). When the hint is below `MinBackoff`, the floor wins.

### Total budget

A single wall-clock budget — `DataSourceReadinessTimeoutMinutes`, default **5 minutes** — covers the entire `LocateAsync` call across all sources. Sources whose next scheduled probe would fall outside the budget are retired immediately rather than scheduled. When the budget is exhausted the locator returns `null` (NoDataSource); `StageRunner` reports `StageRunOutcome.NoDataSource` and exits cleanly.

We use *virtual* elapsed time (`elapsed += sleep` per iteration) rather than reading a real clock. Two reasons: (1) it matches cloud-init's "waited ${TOTAL}s of ${BUDGET}s" model; (2) tests can inject a synthetic delay function without spinning on real wall-clock.

### Settings keys (in `ProvisioningSettings.DataSources`)

| Key                          | Default | Meaning                                                              |
| ---------------------------- | ------- | -------------------------------------------------------------------- |
| `readinessTimeoutMinutes`    | 5       | Total `LocateAsync` budget                                           |
| `minBackoffSeconds`          | 1       | Floor for each `WaitForReady` interval                               |
| `maxBackoffSeconds`          | 60      | Cap on exponential growth                                            |

Legacy key `probeTimeoutMinutes` was removed; it conflated per-source and total caps and was only ever set in tests.

### Cancellation

`CancellationToken` is observed at every loop iteration and is plumbed into the injected `delay` function. Cancellation during a `WaitForReady` backoff aborts `LocateAsync` immediately with `OperationCanceledException`.

### Logging

- Each retry logs at **Debug** with `(source, attempt, reason, backoff)`.
- A datasource's state *kind* transition (e.g. `WaitForReady → Ready`, `Ready → Failed`) logs at **Information**. Operators looking at logs see "the datasource came up" vs "it kept flapping" without scanning every retry line.
- Budget exhaustion logs at **Warning** with the list of still-pending source names.

### Behaviour on per-source give-up

When a source can no longer be probed within the remaining budget, it is dropped from the candidate set. The locator continues probing other sources. We do not "fail the run" until all sources have been dropped or returned `NotApplicable` / `Failed`.

## Deviations from RFC draft

- The draft proposed a per-probe wall-clock timeout (e.g. 30s) wrapping each `ProbeAsync` call. That is **not** implemented: every production probe already enforces its own timeout (Azure IMDS uses a `HttpClient.Timeout`, registry / WMI calls are bounded by the OS). Adding a redundant wrapper risked cancelling a probe mid-write to its internal state. We can revisit if a real source needs the watchdog.
- The draft proposed an operator-overridable per-datasource cap (`dataSources.azure.timeoutMinutes`). Deferred — the global budget covers the v1 use case (one Azure source). A second RFC can add per-source overrides when we have a real conflict.
- `ReportingEvent.Progress` events while waiting: deferred to RFC 0006.

## Open questions (deferred)

- Per-datasource overrides via the settings file.
- Progress events during waits.
