# Differences from cloud-init

The agent inherits cloud-init's vocabulary and structure deliberately —
stage names, module names, frequency labels, semaphore layout, user-
data formats. The list below is the catalog of **deliberate
divergences**.

## Summary table

| Topic | Cloud-init | Agent | Why |
| --- | --- | --- | --- |
| Script dispatch | Shebang-led (`#ps1`, `#!/...`) | **Filename-led** (`.ps1`, `.cmd`, `.bat`) with shebang as fallback | We must accept what eryph fodder genes emit, and those were crafted around cloudbase-init bugs. See [RFC 0007](../../rfcs/0007-scripts-per-frequency-edge-cases.md). |
| Reboot-and-continue | Use the `power_state_change` module | Exit code **1003** from `runcmd` / script triggers reboot mid-stage; agent resumes on next boot | cloudbase-init convention; eryph fodder uses it. |
| Jinja2 templating (`## template: jinja`) | Implemented | **Not supported** — sniffed and ignored | Deferred to [RFC 0011](../../rfcs/0011-jinja2-templating.md). |
| Part-handler (`#part-handler`) | Implemented | **Not supported** — logged + ignored | Deferred indefinitely; security + runtime cost. [RFC 0012](../../rfcs/0012-part-handler.md). |
| Boothook (`#cloud-boothook`) | Executes very early | **Captured but not executed** | Deferred to [RFC 0013](../../rfcs/0013-boothook-execution.md). |
| Argv quoting in `runcmd` | Argv list executed via `subprocess` with shell-style quoting rules | Argv list executed verbatim via `Process.Start`; no shell quoting reapplied | Windows process model differs; the YAML list is the truth. |
| Multipart close delimiter | Required (`--boundary--`) | Optional — last open part is flushed | Real-world eryph fodder occasionally ships without it; silently dropping the last script is worse than tolerating the shape. |
| Module list | Many cloud-init modules; selected by `cloud.cfg` lists | Fixed v1 set: `Growpart`, `SetHostname`, `ApplyNetworkConfig`, `NtpClient`, `Timezone`, `SetLocale`, `UsersGroups`, `SetPasswords`, `SshAuthorizedKeys`, `WriteFiles`, `WriteFilesDeferred`, `Runcmd`, `Licensing`, `ScriptsUser`, `PowerState` | Linux-only and deferred top-level keys (`apt`, `snap`, `packages`, `bootcmd`, `phone_home`, `chef`, …) are **accepted** in the schema and logged at Info by `CloudConfigSerializer`'s acknowledged-key inventory so cross-cloud YAML round-trips cleanly. The inventory is source-generated from the model's `[CloudInitField(Platforms = …)]` attributes — adding a new Linux-only key needs no parallel list update. `egs-tool validate --target=windows` surfaces the same data per file. See [RFC 0028](../../rfcs/0028-linux-keys-module.md). |
| Unknown top-level keys | Warning logged at runtime via `validate_cloudconfig_schema` (jsonschema additionalProperties), CLI-side strict check via `cloud-init schema` | Same tiered behaviour: schema-accepted Linux keys → **Info** via the source-generated platform inventory; truly unknown keys (typo, vendor extension) → **Warning** via `CloudConfigSerializer`; neither aborts | We mirror cloud-init's "log-and-continue" runtime contract; the platform inventory makes the Linux/typo distinction explicit. |
| `chpasswd.expire` | Defaults to `true` — every changed password must be changed at next login | **Implemented** with the same default. `false` suppresses the must-change flag; omitted reads as `true`. Applies to all three input forms (`chpasswd.users`, `chpasswd.list`, top-level `password` shorthand). | — |
| `chpasswd.list` `R` / `RANDOM` token | `user:RANDOM` / `user:R` generates a random password (exact-case match) | **Implemented** with the same exact-case matching. `user:Random` / `user:random` stays a literal password. | — |
| `write_files.defer` | Deferred entries run in the Final stage so they can reference users created earlier in the run | **Implemented** via `WriteFilesDeferredModule` at `[Stage(Final), Order=-1]`. The Config-stage `WriteFilesModule` skips entries flagged `defer: true`; the Final-stage module processes only those. | — |
| `prefer_fqdn_over_hostname` | When true, hostname-applying modules pick the FQDN form | **Implemented** in `SetHostnameModule`: with the flag set, `fqdn` (first label) wins over `hostname`. Default precedence (hostname-first, fqdn-fallback) is unchanged when the flag is absent or false. | — |
| `users[].gecos` | Linux: populates `/etc/passwd`'s comment column | **Implemented** on Windows: maps to the NTUser `FullName` field (visible as "Full name" in `lusrmgr.msc`). | — |
| Semaphore root | `/var/lib/cloud/...` | `%ProgramData%\eryph\provisioning\...` | Windows path. Layout matches. |
| Reporting backends | `LogHandler`, `WebHookHandler`, `HyperVKvpHandler`, Azure (in datasource) | `LogReportingHandler` + `KvpReportingHandler` only | Multi-cloud backends deferred to [RFC 0006](../../rfcs/0006-multi-handler-reporting-cloud-backends.md). No webhook in v1. |
| Vendor-data | Parsed, merged via `cloud_init_merge` strategy | **Parsed but discarded with an Info log** | No eryph use case; merge policy deferred to [RFC 0001](../../rfcs/0001-vendor-data-merge-policy.md). |
| Configurable per-stage module lists | `cloud_init_modules`, `cloud_config_modules`, `cloud_final_modules` in `cloud.cfg` | Stages and ordering are fixed by `[Stage]` attributes | Eryph delivers a fixed module set; configurability deferred ([RFC 0009](../../rfcs/0009-module-list-split.md)). |
| Quoted-printable transfer encoding in multipart | Decoded | Pass-through (treated as UTF-8 bytes of the body) | Real-world cloud-init multiparts use 7bit/8bit/base64; quoted-printable is rare. |
| `power_state` module | Implemented | Implemented — see `PowerState` in [Modules](../reference/modules.md) | Mode `halt` falls back to hibernate (Windows has no clean halt analogue). |

## Things that match deliberately

Listed for completeness — these may also surprise someone coming from
cloudbase-init.

- Four stages: Local / Network / Config / Final.
- Frequencies: per-instance / per-boot / per-once.
- One semaphore file per (module, frequency, [instance]); marker file
  body is JSON for diagnostics, only its existence gates execution.
- `reset` mirrors `cloud-init clean`: per-instance + per-boot cleared,
  per-once kept unless `--reset-once`.
- `collect-logs` mirrors `cloud-init collect-logs`.
- Datasource probe loop with `WaitForReady` + backoff.
- `OnCompletedAsync` hook fires only on full success of the run
  (mirrors cloudbase-init's `provisioning_completed`; cloud-init has
  the same intent).
- Network-config v1 and v2 are both accepted (cloud-init does too).

## Reading more

The [RFCs](../../rfcs/README.md) directory holds the per-decision
rationale. The status column in the [RFC index](../../rfcs/README.md)
is the canonical source for what's implemented vs. drafted vs.
deferred.
