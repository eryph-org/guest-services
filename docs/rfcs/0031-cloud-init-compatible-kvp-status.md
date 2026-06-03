# RFC 0031 — Cloud-init-compatible provisioning status over Hyper-V KVP

Status: Accepted

## Problem

A host-side reader (eryph, but also any plain Hyper-V operator tool) wants a
single, uniform answer to "how is this guest's provisioning going?" that works
the same on a Linux catlet and a Windows catlet.

Today the two producers disagree on the wire:

- **Windows** — the guest-services provisioning agent writes a bespoke
  `eryph.provisioning.*` snapshot to the guest KVP pool
  (`KvpReportingHandler`): a handful of fixed keys carrying the current
  `state`/`stage`/`error`/`updated`.
- **Linux** — guest-services runs **no provisioning at all**. Real cloud-init
  does the work and reports through *its* Hyper-V KVP reporting handler
  (`HyperVKvpReportingHandler`), which writes a completely different shape: a
  `CLOUD_INIT|…` event **stream**.

So a host reader would need two parsers, and the two contracts can drift. We
want guest-services to "behave like cloud-init" on the wire, while still
offering eryph a dead-simple key to read.

## What cloud-init does

`cloudinit/reporting/handlers.py` → `HyperVKvpReportingHandler` writes into the
guest KVP pool (`/var/lib/hyperv/.kvp_pool_1`), which the Hyper-V host surfaces
via WMI `Msvm_KvpExchangeComponent.GuestExchangeItems`.

- **Key:** `CLOUD_INIT|<incarnation>|<event_type>|<event_name>|<vm_id>|<uuid>`
  (`event_key_prefix = "CLOUD_INIT|{incarnation}"`). Oversized descriptions are
  split across extra `…|<index>` subkeys.
- **Value:** compact JSON `{"name","type","ts","result","duration","msg"}`.
- **No status key.** Status is an *event stream* of `start`/`finish` events;
  `result` ∈ `SUCCESS` / `WARN` / `FAIL` (`cloudinit/reporting/events.py`).
- **Stages** are named `init-local`, `init-network`, `modules-config`,
  `modules-final`; the terminal `modules-final` `finish` carries the overall
  result. Each reboot opens a new **incarnation**; stale incarnations are swept.

Limits (the pool, shared by both producers): key ≤ 511 bytes, value ≤ 2047
bytes (`HV_KVP_EXCHANGE_MAX_{KEY,VALUE}_SIZE`); see `DataValidator`.

## What cloudbase-init does

Only Hyper-V KVP reporting (added by the eryph patch). No webhook, no native
cloud callback — same baseline noted in [RFC 0006](0006-multi-handler-reporting-cloud-backends.md).

## Relationship to RFC 0006

Different axis, not a duplicate. [RFC 0006](0006-multi-handler-reporting-cloud-backends.md)
is about *where* reports go — adding new reporting **sinks** for other clouds
(Azure wireserver Ready callback, AWS lifecycle, generic webhook) — and leaves
the Hyper-V KVP handler as-is. This RFC is about *what the Hyper-V KVP sink
writes on the wire* — making it byte-compatible with cloud-init so a single
host-side reader parses Linux (real cloud-init) and Windows (egs) the same way.
0006 never mentions the `CLOUD_INIT|…` format or host-reader unification. The
two compose: 0031 fixes the KVP backend's format; 0006 adds further backends.

## Eryph direction

Two KVP surfaces, both guaranteed by guest-services, so a host reader can pick
its level:

1. **Cloud-init wire stream** — guest-services emits the exact `CLOUD_INIT|…`
   format above so a cloud-init-native reader parses Windows guests identically
   to Linux guests. New `CloudInitKvpReportingHandler`, registered alongside the
   existing handler in the reporting collection (the multi-handler design from
   RFC 0006). On Linux this stream comes from real cloud-init for free — eryph
   base fodder already enables it via `Configs/Linux/ReportingHandlerConfig`
   (`type: hyperv`).

2. **`eryph.provisioning.*` snapshot — KEPT.** The simple, OS-uniform key eryph
   reads (`state` ∈ started/running/reboot_pending/completed/failed, plus
   stage/error/updated/instance/ssh_host_keys). The existing `KvpReportingHandler`
   keeps writing it unchanged on Windows. On Linux, where guest-services does no
   provisioning, a new egs-service component **watches cloud-init status** and
   fills the same snapshot — so `eryph.provisioning.state` is present on both
   OSes regardless of producer.

Net: eryph's host reader targets `eryph.provisioning.state` (trivial, uniform)
and may consume the cloud-init stream for richer per-stage telemetry. The
dual-format complexity lives in guest-services, expressed in cloud-init's own
language, instead of leaking into every host reader.

### Wire format we emit (Windows handler)

- **Key:** `CLOUD_INIT|<incarnation>|<event_type>|<event_name>|<vm_id>|<uuid>`
  — byte-compatible with cloud-init. `incarnation` from the provisioning state
  (monotonic per instance, bumped per reboot); `vm_id` from the SMBIOS system
  UUID; `uuid` fresh per event. Long `msg` split across `…|<index>` subkeys.
- **Value:** compact JSON `{"name","type","ts","result","duration","msg"}`,
  `ts` ISO-8601 UTC.
- **Stale incarnations** swept on startup: enumerate `CLOUD_INIT|` keys, delete
  any whose incarnation precedes the current one (the guest write path deletes a
  key when its value is null).

### Event mapping (guest-services `ReportingEvent` → cloud-init event)

The stage `start`/`finish` events are the backbone (they already span the whole
run), so the eryph-internal lifecycle bookends are dropped to avoid emitting a
duplicate `init-local`/`modules-final` event.

| `ReportingEvent` | cloud-init `name` | `type` | `result` |
|---|---|---|---|
| `StageStarted(stage)` | `<stage>` (`init-local`/`init-network`/`modules-config`/`modules-final`) | start | — |
| `ModuleStarted(m)` | `<stage>/<m>` | start | — |
| `ModuleFinished(m, *)` | `<stage>/<m>` | finish | SUCCESS |
| `ModuleFailed(m, …)` | `<stage>/<m>` | finish | FAIL |
| `StageFinished(stage)` | `<stage>` | finish | SUCCESS |
| `ProvisioningFailed(…)` | `<current-stage>` (or `init-local` if none yet) | finish | FAIL |
| `ProvisioningStarted` / `ProvisioningCompleted` | *(skipped — covered by `StageStarted(Local)` / `StageFinished(Final)`)* | — | — |
| `RebootRequested` / `SshHostKeysReported` / `Progress` | *(no cloud-init analogue — skipped; reboot opens a new incarnation next boot)* | — | — |

Stage name map: `Local→init-local`, `Network→init-network`,
`Config→modules-config`, `Final→modules-final`. The terminal event a reader
keys on is `modules-final` `finish SUCCESS` (success) or a `<stage>` `finish
FAIL` (failure).

## Plan / phases

- **Phase 1 (this PR, Windows):** `CloudInitKvpReportingHandler` + a
  `CloudInitKvpEventEncoder` (key/value/split), incarnation + vm_id sources,
  stale-incarnation sweep, DI registration next to `KvpReportingHandler`
  (gated off in dry-run like its sibling). `KvpReportingHandler` and the
  `eryph.provisioning.*` snapshot are untouched. Unit tests for the encoder and
  the event→cloud-init mapping.
- **Phase 2 (Linux):** an egs-service cloud-init status watcher that polls
  `cloud-init status --format json` and mirrors it into the **single**
  `eryph.provisioning.state` KVP value (`done`→`completed`, `error`→`failed`,
  `running`→`running`, `disabled`→`completed`; `not run`/unknown write nothing).
  It writes only that one key and stops once cloud-init is terminal, so the host
  reads the same provisioning-state key on Linux and Windows. The richer
  per-stage cloud-init stream is consumed directly from real cloud-init on Linux,
  not re-synthesised by egs.
- **Phase 3 (eryph repo, separate):** host-agent reader + compute-API status
  endpoint, reading `eryph.provisioning.state` (and optionally the cloud-init
  stream). Tracked in the eryph repo, not here.

## Open questions

- **WARN / degraded.** Cloud-init has a `WARN`→degraded result; guest-services
  only produces Completed/Failed today, so it can never report *degraded*.
  Acceptable for now; revisit if a non-fatal module outcome is introduced.
- **Reboot.** Cloud-init has no "reboot pending" event — a reboot just ends the
  run and the next boot is a new incarnation. We drop `RebootRequested` from the
  cloud-init stream (the `eryph.provisioning.*` snapshot still carries
  `reboot_pending`).
- **KVP volume.** A full run emits many `CLOUD_INIT|` entries into the Windows
  guest registry pool. Linux proves the model; we should confirm a real run's
  entry count stays within the pool's practical limits and that the incarnation
  sweep keeps it bounded.
- **`vm_id` source.** SMBIOS system UUID via WMI on Windows. Eryph's reader
  ignores the field (it reads per-VM); it exists only for byte-fidelity with a
  generic cloud-init reader.
