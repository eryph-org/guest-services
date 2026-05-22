# Windows cloud-init status (WIP)

> **Work in progress.** The provisioning agent is designed and tested
> for use case (b) eryph. Use case (c) — general-purpose Windows
> cloud-init outside eryph — is not production-ready. The pieces are in
> place but several cloud-init features are not yet implemented, and
> the datasource hardening that real-world non-eryph deployments
> require has not happened. **Do not rely on this for production
> outside eryph today.**

## What works today

The core RFCs are implemented and exercised by the eryph e2e test
suite:

- [RFC 0002 — Network-config v1/v2 application](../../rfcs/0002-network-config-v1-v2-application.md): MAC-matched static IPs, DNS, MTU, gateway.
- [RFC 0003 — Module frequencies](../../rfcs/0003-module-frequencies.md): per-instance, per-boot, per-once with semaphores.
- [RFC 0004 — Datasource readiness](../../rfcs/0004-datasource-readiness-timeout.md): probe loop with exponential backoff and a shared budget.
- [RFC 0005 — Cleanup hook](../../rfcs/0005-datasource-cleanup-hook.md): fires once on full success.
- [RFC 0007 — Scripts filename-led dispatch](../../rfcs/0007-scripts-per-frequency-edge-cases.md): cbi-compat script handling.
- [RFC 0010 — Semaphore layout](../../rfcs/0010-semaphore-design.md): one file per module-frequency.
- [RFC 0014 — Azure datasource v1](../../rfcs/0014-azure-datasource.md): `CustomData.bin` + IMDS + ovf-env fallback, coexists with PA + WinGA.

Datasources implemented end-to-end: **NoCloud**, **ConfigDrive**,
**Azure** (v1, coexistence with PA + WinGA), **CLI override**.

User-data formats: `#cloud-config`, `#include` / `#include-once`,
multipart MIME, `#ps1` / `#ps1_sysnative` / `#!`, `text/x-shellscript`
parts, gzip-wrapped any-of-the-above.

Modules: `SetHostname`, `ApplyNetworkConfig`, `UsersGroups`,
`SetPasswords`, `SshAuthorizedKeys`, `WriteFiles`, `Runcmd`,
`ScriptsUser`.

## What's missing

The following are **not** supported and will be either skipped with a
warning or fall through to NoDataSource.

| Missing | Status | RFC |
| --- | --- | --- |
| Hyper-V KVP user-data ingestion | Stub: detection works, payload read is a TODO. KVP is used for *reporting* (egs-tool get-status) — not for delivering user-data. | (no RFC; in source) |
| EC2 datasource | Stub: returns NotApplicable always. | [0008](../../rfcs/0008-platform-native-provisioner-coexistence.md) |
| GCP datasource | Not implemented. | [0008](../../rfcs/0008-platform-native-provisioner-coexistence.md) |
| Azure wireserver Ready POST | **Deliberately never** (PA + WinGA own it). If you run the agent **without** PA, the Azure fabric will time out. Out of scope. | [0008](../../rfcs/0008-platform-native-provisioner-coexistence.md) / [0014](../../rfcs/0014-azure-datasource.md) |
| Jinja2 templating (`## template: jinja`) | Sniffed and ignored with a warning. | [0011](../../rfcs/0011-jinja2-templating.md) |
| Part-handler (`#part-handler`) | Ignored with a warning. | [0012](../../rfcs/0012-part-handler.md) |
| Boothook execution | Captured but not executed. | [0013](../../rfcs/0013-boothook-execution.md) |
| Vendor-data merge | Parsed and discarded with an Info log. | [0001](../../rfcs/0001-vendor-data-merge-policy.md) |
| `power_state_change` module | Not shipped. Use exit-1003 instead. | [0009](../../rfcs/0009-module-list-split.md) |
| `disk_setup`, `growpart`, `apt`, `yum`, `phone_home`, `ntp`, `timezone`, etc. | Not shipped. | [0009](../../rfcs/0009-module-list-split.md) |
| Configurable per-stage module lists | Not exposed; module set is fixed by `[Stage]` attributes. | [0009](../../rfcs/0009-module-list-split.md) |
| Webhook reporting backend | Not shipped. | [0006](../../rfcs/0006-multi-handler-reporting-cloud-backends.md) |
| OAuth / cloud-native reporting backends | Not shipped. | [0006](../../rfcs/0006-multi-handler-reporting-cloud-backends.md) |
| Random-password secret-channel reporting | Not implemented; passwords logged-only (which is intentionally a no-op for security). Orchestrator can't yet harvest the value. | (in source TODO) |
| Quoted-printable transfer encoding in multipart | Pass-through (treated as UTF-8 bytes); rarely seen in real cloud-init. | — |

## Where to send feedback

This is a working area. If you're using the agent outside eryph and
hit a gap:

- File an issue at <https://github.com/eryph-org/guest-services/issues>.
- Mention the cloud / platform you're on and which marker / format /
  module you expected to work.
- Attach the output of `egs-service collect-logs` if the agent has run
  at least once.

## What "not production-ready outside eryph" means

The eryph e2e suite exercises the agent end-to-end against eryph-zero
emitted fodder. Other producers (real OpenStack metadata servers, real
Azure images without our cooperation rules, NoCloud ISOs hand-built
for cbi) **have not been tested**. In particular:

- We have not validated against the full OpenStack metadata surface
  (`vendor_data2.json`, networking schema variations, etc.).
- The Hyper-V KVP user-data path is not implemented; if your host
  pushes user-data via KVP, the agent will not consume it.
- Datasource detection is conservative; real-world quirks (missing
  files, partial layouts, encoding edge cases) may produce
  `NotApplicable` where cbi or cloud-init would succeed.
