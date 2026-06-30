# Windows cloud-init status

The provisioning agent is designed and tested for eryph. Running it as a
general-purpose cloud-init replacement outside eryph works for the
datasources and features listed below, but the parts marked missing are
not done, and producers other than eryph-zero have not been exercised.
Don't treat the non-eryph path as production-ready yet.

## What works

Implemented and exercised by the eryph e2e suite:

- Network-config v1/v2: MAC-matched static and DHCP addressing, per-interface routes, DNS servers and search suffixes, MTU (IPv4 + IPv6 for v2, IPv4 only for v1). Constructs outside that subset — bonds/bridges/VLANs, per-interface options — are warned, not applied. See the [coverage matrix](../howto/configure-networking.md#coverage-matrix).
- Module frequencies: per-instance, per-boot, per-once with semaphores.
- Datasource readiness: probe loop with exponential backoff and a shared budget.
- Cleanup hook: fires once on full success.
- Filename-led scripts: cbi-compatible script dispatch.
- Module list split: per-stage `enabledModules` / `disabledModules`.
- Semaphore layout: one file per module-frequency.
- Azure datasource: `CustomData.bin` + IMDS + ovf-env, coexists with the Azure PA + WinGA. Implemented and verified on Azure.

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
accepted and logged at Info, so a cross-cloud cloud-config passes through
cleanly. `egs-service validate --target windows` lists them per file.

The per-stage module list and the datasource list are both operator-
configurable — see [Settings](../reference/settings.md).

## What's missing

These are skipped, with a warning where noted:

| Missing | Status |
| --- | --- |
| EC2 datasource | Stub: returns `NotApplicable`. |
| GCP datasource | Not implemented. |
| Azure wireserver Ready POST | **Deliberately never** — PA + WinGA own that channel. Run the agent without PA and the Azure fabric will time out. |
| Jinja2 templating (`## template: jinja`) | Sniffed and ignored. |
| Part-handler (`#part-handler`) | Logged and ignored. |
| Boothook execution | Captured but not executed. |
| Vendor-data merge | Parsed and discarded with an Info log. |
| Random passwords (`type: RANDOM`, `chpasswd.list` `R`/`RANDOM`, password-less entries) | **Rejected** by `validate`, warn-skipped at runtime. No `/dev/console` analogue on Windows to deliver the generated value. |
| `windows_update`, `winget`, `chocolatey`, `chef`, `dsc` modules | Not implemented. |
| Webhook / cloud-native reporting backends | Not shipped (Log + Hyper-V KVP only). |
| WinRM listener configuration | Not implemented. |
| Secure-bootstrap config handshake | Not implemented. |
| Hyper-V KVP user-data ingestion | Not implemented. KVP is a reporting channel, not a user-data channel. |
| Quoted-printable transfer encoding in multipart | Pass-through (UTF-8 bytes); rarely seen in real cloud-init. |

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
