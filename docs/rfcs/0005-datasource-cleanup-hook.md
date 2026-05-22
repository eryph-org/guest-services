# RFC 0005 — Datasource cleanup hook timing

Status: Implemented

## Problem

`IDataSource.OnCompletedAsync` lets a datasource clean up after successful provisioning (e.g., Azure datasource deletes `C:\AzureData\CustomData.bin`). When does this fire, and what counts as "successful"?

## What cloudbase-init does (reference)

`provisioning_completed()` on the metadata service fires only when every plugin finished AND no plugin requested a reboot in the most recent run (`init.py:228–232`). The eryph Azure patch uses this to remove `CustomData.bin`.

## What cloud-init does

Cloud-init has `DataSource.deactivate()` (called when instance state changes) and a per-datasource `clean()` hook. The relevant moment for cleanup is at end of `Final` stage, after all modules ran.

## Design (implemented)

**Option 1 — only on full success.** Mirrors cbi.

`StageRunner.RunStagesAsync` calls `dataSourceLocator.OnProvisioningCompletedAsync(data, ct)` exactly once, immediately after the `ProvisioningCompleted` reporting event, on the path that returns `StageRunOutcome.Success.Instance`. The other exit paths (`NoDataSource`, `RebootRequested`, `Failed`) all return earlier and never reach the hook.

The hook is wrapped in `try/catch (Exception)` with cancellation re-thrown. If the hook (or anything dispatched by `DataSourceLocator.OnProvisioningCompletedAsync`) throws, the runner logs at Warning and still returns `Success` — provisioning has already succeeded by the time the hook fires; a stuck `CustomData.bin` is not worth flipping the outcome.

## Per-datasource cleanup actions

| Source           | Action                                                                                                                                                                  | Cloud-init parity                                                                                              |
| ---------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------- |
| `AzureDataSource`  | `File.Delete(C:\AzureData\CustomData.bin)`; then remove the parent directory iff it is now empty. Mirrors cbi's `AzureCustomDataService.provisioning_completed()`.       | cbi-equivalent. Cloud-init's Azure datasource doesn't manage CustomData.bin (Linux uses ovf-env on /dev/sr0). |
| `NoCloudDataSource` | No-op. eryph-zero keeps the `cidata` ISO attached so `egs-tool reset` can re-read the same payload. cloud-init's NoCloud datasource doesn't eject either.             | Matches cloud-init NoCloud `clean()` — no-op.                                                                |
| `ConfigDriveDataSource` | No-op. Same rationale as NoCloud: host owns the `config-2` ISO lifetime.                                                                                            | Matches cloud-init ConfigDrive `clean()` — no-op.                                                            |

## Idempotency

Every implementation tolerates being called twice. The Azure source checks `File.Exists` before deleting and treats a missing file as the success path (logs Debug, continues). The no-op sources are trivially idempotent. Locator-level: `DataSourceLocator.OnProvisioningCompletedAsync` may be invoked any number of times with the same `DataSourceResult`; it dispatches each time.

## Cancellation

The hook receives the StageRunner's cancellation token. A cancellation cancels the hook the same way it cancels the rest of the run — `OperationCanceledException` propagates. (Note: in practice the run is already complete at this point, so the only realistic cancellation here is a service shutdown.)

## Wiring

```text
StageRunner.RunStagesAsync(...)
  -> after Final stage modules complete
  -> reporter.EmitAsync(ProvisioningCompleted)
  -> dataSourceLocator.OnProvisioningCompletedAsync(data, ct)
      -> source.OnCompletedAsync(data, ct)   [routed via the locator's completion map]
  -> return StageRunOutcome.Success.Instance
```

The locator persists the (`DataSourceResult` → producing `IDataSource`) mapping in `_completionMap` at the moment of the winning probe, so dispatch is deterministic even when sources compose state from each other (e.g. Azure's CustomData.bin path).

## Tests (added)

- `DataSourceLocatorTests.OnProvisioningCompletedAsync_is_idempotent_when_called_twice` — second call must not throw, must dispatch again.
- `AzureDataSourceTests.OnCompletedAsync_deletes_CustomData_bin_and_empty_parent_directory` — real-shape temp directory; verifies file + dir removal.
- `AzureDataSourceTests.OnCompletedAsync_is_idempotent_when_CustomData_bin_already_absent` — second call on missing payload.
- `AzureDataSourceTests.OnCompletedAsync_called_twice_succeeds_and_leaves_filesystem_consistent` — full idempotency cycle.
- `AzureDataSourceTests.OnCompletedAsync_swallows_IO_exceptions_and_does_not_throw` — best-effort contract.
- `AzureDataSourceTests.OnCompletedAsync_does_not_delete_parent_when_directory_still_has_other_files` — safety guard.
- `ConfigDriveDataSourceTests.OnCompletedAsync_is_a_noop_and_does_not_touch_filesystem` — no-op contract.
- `StageRunnerTests.RunAsync_invokes_data_source_cleanup_only_on_full_Success` — happy path.
- `StageRunnerTests.RunAsync_does_not_invoke_cleanup_on_RebootRequested` — reboot-and-continue path.
- `StageRunnerTests.RunAsync_does_not_invoke_cleanup_on_Failed` — failure path.
- `StageRunnerTests.RunAsync_keeps_Success_when_cleanup_hook_throws` — best-effort exception swallowing at the runner level.
