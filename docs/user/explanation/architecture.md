# Architecture

A run of the agent has four moving parts: the **datasource locator**,
the **user-data pipeline**, the **stage runner**, and the **reporting
dispatcher**. They run in the same process, in the order below.

```
+-------------------+
|  Datasource probe |   discover one source, get InstanceId + bytes
+--------+----------+
         |
         v
+-------------------+
|  User-data parse  |   bytes -> ResolvedUserData (cloud-config + scripts)
+--------+----------+
         |
         v
+-------------------+
| Stage runner      |   Local -> Network -> Config -> Final
|  (semaphores      |     per module: skip if semaphore exists; else apply
|   gate modules)   |     RebootRequested -> shutdown /r; resume next boot
+--------+----------+
         |
         v
+-------------------+
| Cleanup hook      |   datasource.OnCompletedAsync (e.g. delete CustomData.bin)
+--------+----------+
         |
         v
+-------------------+
| Reporting         |   Log + Hyper-V KVP; emitted throughout the run
+-------------------+
```

## Datasource locator

`IDataSourceLocator.LocateAsync(ct)` probes every registered
`IDataSource` in ascending priority order. Each probe returns one of:

- `Ready(DataSourceResult)` — done; the locator returns this result.
- `NotApplicable` — this source isn't the one; try the next.
- `WaitForReady(backoff)` — not ready yet; retry after the (clamped)
  backoff. The locator interleaves retries across all `WaitForReady`
  sources, sharing a single wall-clock budget.
- `Failed(reason)` — drop this source from the candidate set; the
  locator continues with the rest.

When all sources have been dropped or the budget is exhausted, the run
exits cleanly with `NoDataSource` — that's the "this is not a
provisioning environment" path. Use cases (a) Hyper-V standalone and
(c) WIP cloud-init fall through here when no fodder is attached.

See [datasources reference](../reference/datasources.md) and
[RFC 0004](../../rfcs/0004-datasource-readiness-timeout.md) /
[RFC 0005](../../rfcs/0005-datasource-cleanup-hook.md).

## User-data pipeline

`IUserDataPipeline.ResolveAsync(bytes, ct)` turns the raw user-data
bytes into a `ResolvedUserData`:

1. Gunzip if the bytes are gzip-magic.
2. Sniff the first non-empty line for a marker (see
   [User-data formats](../reference/user-data-formats.md)).
3. Wrap as a `UserDataPart` with the sniffed content type.
4. Dispatch through handlers — the first one whose `CanHandle` matches
   processes it. Handlers may **recurse** (multipart spawns nested
   parts; `#include` fetches new payloads).
5. Each handler can do one or both of:
   - Merge a cloud-config fragment.
   - Append a script / boothook payload.
6. Once the recursion settles, return the merged cloud-config + the
   captured payloads.

The recursion limit is `userData.maxRecursionDepth`
(default 10). Visited `#include` URLs are tracked to break cycles.

## Stage runner

`StageRunner.RunAsync(ct)` is the orchestrator. It:

1. Locates the datasource (or short-circuits when an `OverrideDataSource`
   is injected from `--user-data`).
2. Detects a new boot session and clears per-boot semaphores when
   needed.
3. Loads or initialises `state.json` for this instance.
4. For each stage in order, resolves user-data on first need, then
   runs every module in declared order. Each module is gated by its
   semaphore.
5. On reboot request: writes the module's semaphore, returns
   `RebootRequested`. The CLI translates that into a reboot.
6. After Final completes: emits `ProvisioningCompleted` and calls the
   datasource cleanup hook.

## Reporting

`IReportingDispatcher.EmitAsync(event)` fans out to every registered
`IReportingHandler`. v1 ships two:

- **`LogReportingHandler`** — always on. Routes the event to the
  configured `ILogger` pipeline.
- **`KvpReportingHandler`** — self-gates on a Hyper-V guest (the
  `HKLM\SOFTWARE\Microsoft\Virtual Machine\Guest\Parameters\VirtualMachineId`
  registry value). When present, writes provisioning state and
  per-stage progress into the Hyper-V data-exchange KVP channel so the
  host can read it. This is how `egs-tool get-status` works.

Multi-handler reporting (Azure wireserver callback, AWS lifecycle
signals, generic webhook) is deferred to
[RFC 0006](../../rfcs/0006-multi-handler-reporting-cloud-backends.md).

## State store

`state.json` lives at `%ProgramData%\eryph\provisioning\state.json` and
records:

- `instanceId` — the source-supplied id.
- `startedAt`, `lastUpdated`.
- `rebootCount`.
- `completedStages` — the stages that finished cleanly.
- `completedHandlers` — kept for the legacy migration path; the source
  of truth for module skipping is the per-module semaphore file.

The state store is **disk-backed**. `egs-service status` always reads
from disk, never an in-memory cache — so the value you see is the value
the next boot will see.

## DI composition

`ProvisioningContainerBuilder.Build()` wires the pipeline using
SimpleInjector. The Generic Host adapter is there only to satisfy
`ILogger<T>` injection — the agent does not use the full hosted-service
lifecycle for stage execution (a `run` is one-shot and exits when
done).
