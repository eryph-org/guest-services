# RFC 0006 — Multi-handler reporting: Azure / AWS cloud backends

Status: Draft

## Problem

v1 ships `LogReportingHandler` (always) + `KvpReportingHandler` (Hyper-V self-gated). For multi-cloud, we need additional handlers that report to platform-native channels:

- **Azure**: wire-server callback (HTTP PUT to the agent endpoint) marking provisioning state. Azure considers a VM "Ready" only after this callback succeeds.
- **AWS**: signals via CloudWatch logs, EC2 instance lifecycle events, or simply the user-data console output.
- **OpenStack**: `nova` API callback for `os-stop` / `os-start` notifications.
- **Webhook**: generic HTTP POST sink for any orchestrator.

## What cloud-init does

`cloudinit/reporting/handlers.py` defines `LogHandler`, `HyperVKvpHandler`, `PrintHandler`, `WebHookHandler`. Azure-specific reporting lives in the AzureDatasource (`report_finished`, `report_failure`) since it's tied to the wireserver protocol.

## What cloudbase-init does

Only HyperV KVP reporting (the eryph patch added it). No webhook, no Azure native callback.

## Eryph direction

`IReportingHandler` already supports multi-handler dispatch (built in v1). Each cloud-specific handler:

- Self-gates via `IsApplicable` (Azure handler probes for VmId registry key; AWS handler probes BIOS vendor).
- Subscribes to relevant `ReportingEvent` types (most handlers care about `ProvisioningCompleted` and `ProvisioningFailed`; some care about per-stage progress).
- Is registered unconditionally in DI — `IsApplicable` does the work to filter.

v1 ships:
- `LogReportingHandler` (built)
- `KvpReportingHandler` (built)

v2 candidates (one per RFC follow-up):
- `AzureWireServerReportingHandler` — needs the wireserver endpoint URL (passed via Azure datasource), HTTP PUT with goal-state XML
- `AwsLifecycleReportingHandler` — TBD what AWS expects; possibly nothing (EC2Launch is its own thing)
- `WebhookReportingHandler` — configurable URL in `egs-provisioning.json`

## Open questions

- Azure wireserver protocol: do we need to track HEALTH state through provisioning (NotReady → Ready), or only signal a single "Ready" at the end?
- Should reporting handlers be able to MUTATE the run (e.g., a handler that rejects events and forces a stop)? Cloud-init says no — handlers are sinks only. Confirm same for us.
- Rate limiting / batching of progress events to avoid wireserver overload.
