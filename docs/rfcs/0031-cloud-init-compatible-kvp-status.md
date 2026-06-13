# RFC 0031 — Cloud-init-compatible provisioning reporting over Hyper-V KVP

Status: Accepted

## Problem

A host-side reader (eryph, or any plain Hyper-V operator tool) wants a single,
uniform answer to two questions that works the same on a Linux catlet and a
Windows catlet:

1. **How is provisioning going?** — a simple status.
2. **If it failed, why?** — the failure reason.

Today the two producers disagree on the wire:

- **Windows** — the guest-services provisioning agent writes a bespoke
  `eryph.provisioning.*` snapshot to the guest KVP pool (`KvpReportingHandler`):
  `state`, plus `instance`, `stage`, `reboot_reason`, `error`, `updated`.
- **Linux** — guest-services runs **no provisioning**. Real cloud-init does the
  work and reports through *its* Hyper-V KVP handler
  (`HyperVKvpReportingHandler`), a completely different shape: a `CLOUD_INIT|…`
  event **stream**, with **no single status value**.

So a host reader needs two parsers, the contracts drift, and the extra bespoke
Windows keys add noise that confuses readers.

## Principle

**cloud-init is the reference producer.** egs never re-processes or re-derives
cloud-init's reporting on Linux. Where cloud-init runs (Linux) it owns
reporting; where it does not (Windows) egs stands in and emits the same wire
format. The one thing egs adds on Linux is a thin bridge for the simple status
value that cloud-init's event stream does not expose.

## Two surfaces, two jobs

### Surface 1 — cloud-init reporting: the `CLOUD_INIT|…` event stream

The rich, per-stage/per-module reporting. **This is where failure reasons
live.** Byte-shape matches `cloudinit/reporting/handlers.py:HyperVKvpReportingHandler`:

- **Key:** `CLOUD_INIT|<incarnation>|<event_type>|<event_name>|<uuid>` —
  cloud-init `_event_key()` layout.
- **Value:** compact JSON `{"name","type","ts",["result"],"msg"}` in cloud-init's
  field order. `result ∈ SUCCESS | WARN | FAIL` (finish only). Per-event timing
  is recoverable from the `start`/`finish` `ts` pair, as in cloud-init. `ts`
  matches cloud-init's `datetime.fromtimestamp(ts, utc).isoformat()` — a `+00:00`
  offset and 6-digit microseconds when non-zero, with the fractional part omitted
  entirely for whole seconds.
- **Oversize handling** uses cloud-init's `_break_down` shape: a value over the
  Azure limit (`HV_KVP_AZURE_MAX_VALUE_SIZE = 1024` bytes) is split across
  `<base>|<index>` subkeys, each a full event JSON carrying `msg_i:<index>` and
  a slice of the description; a reader concatenates the `msg` slices ordered by
  `msg_i` (grouped by the shared `<uuid>`). A value that fits is a single entry
  under the base key (no index, no `msg_i`). One deliberate difference: cloud-init
  slices the *already-escaped* string and can emit invalid JSON when a cut lands
  inside a `\` or `\uXXXX` escape — egs instead slices the *raw* description and
  re-escapes each chunk, so every chunk is valid JSON and still reassembles.
  Size is measured in UTF-8 bytes; the JSON writer escapes non-ASCII (and an
  HTML-safe superset of cloud-init's escapes), so values stay ASCII and within
  the limit.

The **`incarnation`** is the boot time as epoch seconds, matching cloud-init's
`int(time.time() - uptime)` — egs reads it from the boot clock
(`IBootClock.GetCurrentBootTime`, `Win32_OperatingSystem.LastBootUpTime`). It is
stable within a boot and higher after a reboot, so the stale-incarnation sweep
deletes prior boots' entries exactly as cloud-init's does.

Matches `cloudinit/reporting/handlers.py:HyperVKvpReportingHandler`.

Producers:

- **Linux** — real cloud-init writes it natively. eryph base fodder enables it
  via `Configs/Linux/ReportingHandlerConfig` (`type: hyperv`). **egs does
  nothing.**
- **Windows** — no cloud-init exists, and egs *is* the provisioner, so egs
  emits the identical stream (`CloudInitKvpReportingHandler` +
  `CloudInitKvpEventEncoder`). egs exists here only to look like cloud-init on
  the wire.

**Failure reasons** are read from `finish … FAIL` events — cloud-init-native on
Linux, egs-emitted on Windows. A single reader rule ("find a `finish … FAIL`
event; its `name` is the failed stage/module, its `msg` is the reason") works on
both OSes. egs may put a richer `msg` than cloud-init does (extra detail is
fine); it never makes the reader rule OS-specific.

### Surface 2 — `eryph.provisioning.state`: the simple status value

cloud-init's reporting is an event **stream** with **no single status result**,
yet eryph wants one trivial key to poll. Values:
`started | running | reboot_pending | completed | failed`.

Producers:

- **Windows** — egs's `KvpReportingHandler` writes it from the provisioning
  lifecycle.
- **Linux** — egs polls `cloud-init status --format json` and maps the `status`
  field to this key (`CloudInitStatusWatcher` + `CloudInitStateMapper`:
  `done`→`completed`, `error`→`failed`, `running`→`running`,
  `disabled`→`completed`; `not run`/unknown → write nothing). It writes **only
  this one key** and stops once cloud-init is terminal.

This Linux poller is a **status bridge, not a reporting processor**: it reads
cloud-init's single status value, never its event stream and never its failure
reasons. That distinction is the whole point — egs does not translate
cloud-init's reporting on Linux.

## The bespoke Windows keys are dropped

`KvpReportingHandler` is reduced to the single status key. The others duplicated
(and diverged from) what Surface 1 now provides, were Windows-only — so a reader
could never rely on them cross-OS — and were just noise:

| Key | Decision | Why |
| --- | --- | --- |
| `eryph.provisioning.state` | **KEEP** | Surface 2. The one uniform status key. |
| `eryph.provisioning.instance` | drop | instance / vm id is in the Surface 1 key. |
| `eryph.provisioning.stage` | drop | per-stage `start`/`finish` is in Surface 1. |
| `eryph.provisioning.reboot_reason` | drop | reboot ends the run (new incarnation in Surface 1); `state` still goes `reboot_pending`. |
| `eryph.provisioning.error` | **drop** | **failure reasons are read from Surface 1's `FAIL` events**, uniformly on both OSes — not from a Windows-only key. |
| `eryph.provisioning.updated` | drop | every Surface 1 event carries `ts`. |
| `eryph.provisioning.ssh_host_keys` | **drop the KVP key only** | Its KVP value had no consumer. The `SshHostKeysReported` reporting **event stays** (the log handler consumes it, and other sinks — RFC 0006 backends — can subscribe); only this status handler stops writing the consumer-less KVP key. |

**Net reader contract:**

- *"Is it done / failed?"* → `eryph.provisioning.state` (one key, both OSes).
- *"Why did it fail?"* → the `CLOUD_INIT|… finish … FAIL` event (cloud-init's
  own mechanism, both OSes).

## Event mapping (egs `ReportingEvent` → cloud-init event) — Windows Surface 1

Stage `start`/`finish` events are the backbone, so the eryph-internal lifecycle
bookends are dropped to avoid duplicating `init-local` / `modules-final`.

| `ReportingEvent` | cloud-init `name` | `type` | `result` |
|---|---|---|---|
| `StageStarted(stage)` | `<stage>` | start | — |
| `ModuleStarted(m)` | `<stage>/<m>` | start | — |
| `ModuleFinished(m)` | `<stage>/<m>` | finish | SUCCESS |
| `ModuleFailed(m, reason)` | `<stage>/<m>` | finish | FAIL (`msg`=reason) |
| `StageFinished(stage)` | `<stage>` | finish | SUCCESS |
| `ProvisioningFailed(reason)` | `<current-stage>` (or `init-local`) | finish | FAIL (`msg`=reason) |
| `ProvisioningStarted` / `ProvisioningCompleted` | *(skipped — covered by stage start/finish)* | — | — |
| `RebootRequested` / `SshHostKeysReported` / `Progress` | *(no cloud-init analogue — skipped)* | — | — |

Stage name map: `Local→init-local`, `Network→init-network`,
`Config→modules-config`, `Final→modules-final`. Each reboot opens a new
incarnation (the boot epoch second); stale incarnations are swept on startup.

## Components

- **Windows, Surface 1:** `CloudInitKvpReportingHandler`,
  `CloudInitKvpEventEncoder`. Registered alongside
  `KvpReportingHandler` in the reporting collection (gated off in dry-run).
- **Windows, Surface 2:** `KvpReportingHandler`, reduced to `state`
  (+ the out-of-scope `ssh_host_keys`).
- **Linux, Surface 2:** `CloudInitStatusWatcher` + `CloudInitStatusReader` +
  `CloudInitStateMapper`, registered only on Linux.
- **Linux, Surface 1:** nothing — cloud-init native.

## Relationship to RFC 0006

Different axis. [RFC 0006](0006-multi-handler-reporting-cloud-backends.md) adds
new reporting **sinks** (Azure Ready callback, AWS lifecycle, webhook). This RFC
defines *what the Hyper-V KVP sink writes* and *how a host reader consumes it
uniformly*. They compose.

## Non-goals / deferred

- **WARN / degraded.** egs only produces Completed/Failed; no `WARN` result.
  Acceptable; revisit if a non-fatal module outcome is introduced.
- **Strict byte-fidelity / full cloud-init feature parity.** Not required. The
  reader rule (state key + `FAIL` event) is what must match, not every field.
- **Host reader + compute-API status endpoint.** eryph repo, separate.

## Open questions

- **What cloud-init writes in its KVP `FAIL` `msg` on Linux.** The detailed
  reason may live in guest-local `result.json`, not KVP. Whatever cloud-init
  exposes *over KVP*, egs matches; egs never reaches into cloud-init on Linux to
  enrich it. Verify on a forced Linux failure that the reader rule pulls a
  useful reason from both producers.
- **KVP volume.** A full Windows run emits many `CLOUD_INIT|` entries into the
  guest registry pool; the incarnation sweep bounds it. Confirm on a real run.
