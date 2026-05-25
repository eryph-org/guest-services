# Windows cloud-init status

The provisioning agent is designed and tested for eryph. Running it as a
general-purpose cloud-init replacement outside eryph works for the
datasources and features listed below, but the parts marked missing are
not done, and producers other than eryph-zero have not been exercised.
Don't treat the non-eryph path as production-ready yet.

## What works

Core RFCs, implemented and exercised by the eryph e2e suite:

- [RFC 0002 — Network-config v1/v2](../../rfcs/0002-network-config-v1-v2-application.md): MAC-matched IPv4 + IPv6, static and DHCP, per-interface routes, DNS servers and search suffixes, MTU.
- [RFC 0003 — Module frequencies](../../rfcs/0003-module-frequencies.md): per-instance, per-boot, per-once with semaphores.
- [RFC 0004 — Datasource readiness](../../rfcs/0004-datasource-readiness-timeout.md): probe loop with exponential backoff and a shared budget.
- [RFC 0005 — Cleanup hook](../../rfcs/0005-datasource-cleanup-hook.md): fires once on full success.
- [RFC 0007 — Filename-led scripts](../../rfcs/0007-scripts-per-frequency-edge-cases.md): cbi-compatible script dispatch.
- [RFC 0009 — Module list split](../../rfcs/0009-module-list-split.md): per-stage `enabledModules` / `disabledModules`.
- [RFC 0010 — Semaphore layout](../../rfcs/0010-semaphore-design.md): one file per module-frequency.
- [RFC 0014 — Azure datasource](../../rfcs/0014-azure-datasource.md): `CustomData.bin` + IMDS + ovf-env, coexists with the Azure PA + WinGA. Implemented and verified on Azure.

Datasources end to end: **NoCloud**, **ConfigDrive**, **Azure**, and the
**CLI `--user-data` override**.

User-data formats: `#cloud-config`, `#include` / `#include-once`,
multipart MIME, `#ps1` / `#ps1_sysnative` / `#!`, `text/x-shellscript`
parts, and any of those gzip-wrapped.

Modules, in run order: `Growpart`, `SetHostname`, `ApplyNetworkConfig`,
`NtpClient`, `Timezone`, `SetLocale`, `UsersGroups`, `SetPasswords`,
`SshModule`, `WriteFiles`, `Runcmd`, `Licensing`, `WriteFilesDeferred`,
`ScriptsUser`, `PowerState`. See [Modules](../reference/modules.md).

Linux-only top-level keys (`apt`, `snap`, `packages`, `chef`, …) are
accepted by the schema and logged at Info from a source-generated
inventory, so cross-cloud cloud-config round-trips cleanly.
`egs-service validate --target windows` surfaces the same per file.

The per-stage module list and the datasource list are both operator-
configurable — see [Settings](../reference/settings.md).

## What's missing

These are skipped (with a warning where noted) or fall through to
`NoDataSource`.

| Missing | Status | RFC |
| --- | --- | --- |
| EC2 datasource | Stub: returns `NotApplicable`. | [0008](../../rfcs/0008-platform-native-provisioner-coexistence.md) |
| GCP datasource | Not implemented. | [0008](../../rfcs/0008-platform-native-provisioner-coexistence.md) |
| Azure wireserver Ready POST | **Deliberately never** — PA + WinGA own that channel. Run the agent without PA and the Azure fabric will time out. | [0008](../../rfcs/0008-platform-native-provisioner-coexistence.md) / [0014](../../rfcs/0014-azure-datasource.md) |
| Jinja2 templating (`## template: jinja`) | Sniffed and ignored. | [0011](../../rfcs/0011-jinja2-templating.md) |
| Part-handler (`#part-handler`) | Logged and ignored. | [0012](../../rfcs/0012-part-handler.md) |
| Boothook execution | Captured but not executed. | [0013](../../rfcs/0013-boothook-execution.md) |
| Vendor-data merge | Parsed and discarded with an Info log. | [0001](../../rfcs/0001-vendor-data-merge-policy.md) |
| Random passwords (`type: RANDOM`, `chpasswd.list` `R`/`RANDOM`, password-less entries) | **Rejected** by `validate`, warn-skipped at runtime. No `/dev/console` analogue on Windows to deliver the generated value. | — |
| `windows_update`, `winget`, `chocolatey`, `chef`, `dsc` modules | Not implemented. | [0020](../../rfcs/0020-winget-module.md) / [0021](../../rfcs/0021-chocolatey-module.md) / [0022](../../rfcs/0022-chef-module.md) / [0025](../../rfcs/0025-dsc-module.md) |
| Webhook / cloud-native reporting backends | Not shipped (Log + Hyper-V KVP only). | [0006](../../rfcs/0006-multi-handler-reporting-cloud-backends.md) |
| WinRM listener configuration | Not implemented. | [0026](../../rfcs/0026-winrm-listener-deferred.md) |
| Secure-bootstrap config handshake | Drafted, not implemented. | [0029](../../rfcs/0029-secure-config-bootstrap-handshake.md) |
| Hyper-V KVP user-data ingestion | Not implemented. KVP is a reporting channel, not a user-data channel. | — |
| Quoted-printable transfer encoding in multipart | Pass-through (UTF-8 bytes); rarely seen in real cloud-init. | — |

## Not validated outside eryph

The e2e suite drives the agent against eryph-zero fodder. Other
producers have not been tested:

- Real OpenStack metadata servers (`vendor_data2.json`, the full
  networking schema surface, version-layout variations).
- Azure images outside eryph's cooperation rules with PA + WinGA. The
  Azure datasource itself is verified, but only in the eryph context.
- Hand-built NoCloud ISOs targeting cbi quirks.

Datasource detection is conservative: real-world quirks (missing files,
partial layouts, encoding edge cases) may produce `NotApplicable` where
cbi or cloud-init would succeed.

## Feedback

File an issue at <https://github.com/eryph-org/guest-services/issues>.
Name the cloud / platform and the marker / format / module you expected
to work, and attach `egs-service collect-logs` output if the agent has
run at least once.
