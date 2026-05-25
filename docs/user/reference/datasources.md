# Datasources

The agent locates exactly one datasource at the start of each run. The
locator probes every registered source, lowest `Priority` value first,
and returns the first one that says `Ready`. If a source says
`WaitForReady`, the locator backs off (1s → 60s, exponential) and tries
again, sharing a global wall-clock budget with every other source
(default 15 minutes). See [RFC 0004](../../rfcs/0004-datasource-readiness-timeout.md).

If no source becomes ready within the budget, the run exits cleanly
with `NoDataSource` — no provisioning happens, no failure is reported.

| Source | Priority | Requires network |
| --- | --- | --- |
| `Azure` | 10 | yes |
| `EC2` | 20 | yes (stub) |
| `NoCloud` | 30 | no |
| `ConfigDrive` | 40 | no |

By default every registered source is probed in `Priority` order. The
set and order are operator-configurable via the `dataSources.dataSourceList`
[setting](settings.md#datasources--locator-tunables) — mirroring
cloud-init's `datasource_list`. When it is set, only the named sources
are probed, in the listed order (priority is ignored for selection);
unknown names are logged at Warning and skipped. When unset, the
priority order below applies.

The `OverrideDataSource` used by `egs-service run --user-data` is
synthetic and short-circuits discovery entirely.

---

## Azure

**Priority:** 10. **Requires network:** yes (link-local IMDS).

**Detection.** Two signals, either suffices:

1. Registry value `HKLM\SOFTWARE\Microsoft\Windows Azure\VmId` exists and is non-empty.
2. SMBIOS `Win32_SystemEnclosure.SMBIOSAssetTag` equals `7783-7084-3265-9085-8269-3286-77`.

Neither match → `NotApplicable`.

**Data read.** Three sources, in this order:

1. `C:\AzureData\CustomData.bin` — written by Microsoft's Provisioning
   Agent (PA) during OOBE. Raw bytes. **Not encrypted** at any layer.
2. IMDS `http://169.254.169.254/metadata/instance?api-version=2021-02-01`
   with the mandatory `Metadata: true` header. Used for `compute.vmId`,
   `compute.name`, `compute.location`, `compute.zone`, `compute.vmSize`.
3. `ovf-env.xml` from a still-mounted ConfigDrive (fallback, rare on
   Azure post-PA).

**InstanceId** resolution: IMDS `vmId` → registry `VmId` → `Failed`.
**Hostname**: ovf-env → IMDS `name`. PA has usually already applied it;
the value is largely informational.

**Coexistence (HARD RULE).** PA and the long-running Windows Guest
Agent (`WindowsAzureGuestAgent.exe`) own the Azure wireserver channel
indefinitely. The agent **never** POSTs to the wireserver, never sends
telemetry as a Microsoft component, never re-applies hostname / admin
user / RDP — those are PA's job. See
[Coexistence](../explanation/coexistence.md) and
[RFC 0008](../../rfcs/0008-platform-native-provisioner-coexistence.md) /
[RFC 0014](../../rfcs/0014-azure-datasource.md).

**Cleanup hook.** On successful provisioning, `CustomData.bin` is
deleted (and the parent directory removed if empty). Mirrors cloudbase-
init. Best-effort; cleanup failures log a warning and the run still
reports Success. See
[RFC 0005](../../rfcs/0005-datasource-cleanup-hook.md).

---

## NoCloud

**Priority:** 30. **Requires network:** no.

**Detection.** A mounted volume whose label is `cidata`. On Azure the
source declines (`NotApplicable`) defensively — that disk, if any, is
PA's.

**Layout expected on the volume root:**

| File | Required | Used for |
| --- | --- | --- |
| `meta-data` | yes | `instance-id`, `local-hostname` |
| `user-data` | no | Raw user-data bytes (sniffed downstream) |
| `vendor-data` | no | Vendor user-data bytes (parsed; merge deferred — see [RFC 0001](../../rfcs/0001-vendor-data-merge-policy.md)) |
| `network-config` | no | network-config v1/v2 YAML |

`user-data` and `vendor-data` are read as **raw bytes** — never round-
tripped through `ReadAllText`. Real-world user-data is frequently
gzipped multipart MIME whose bytes are not valid UTF-8.

**Cleanup hook.** No-op. eryph-zero keeps the cidata ISO attached so
`egs-service reset` can re-read the same payload. Matches cloud-init.

---

## ConfigDrive

**Priority:** 40. **Requires network:** no.

**Detection.** A mounted volume whose label is `config-2`. Declines on
Azure for the same reason as NoCloud.

**Version walk.** Like cloud-init, the source walks the dated metadata
versions `openstack/<version>/` newest-first (`2018-08-27` down to
`2012-08-10`) and reads the first one whose `meta_data.json` is present,
falling back to `openstack/latest/`. ISOs that omit the `latest` symlink are
still picked up.

**Layout expected** (`<version>` is the resolved version above):

| File | Required | Used for |
| --- | --- | --- |
| `openstack/<version>/meta_data.json` | yes | `uuid` (instance id), `hostname` / `name`, `availability_zone`, `public_keys` |
| `openstack/<version>/user_data` | no | Raw user-data bytes |
| `openstack/<version>/vendor_data.json` | no | Vendor data (parsed; merge deferred) |
| `openstack/<version>/network_data.json` | no | network-config (best-effort YAML parse — JSON form is not v1 supported) |

Bytes are read raw, same as NoCloud.

**SSH keys.** `meta_data.json` `public_keys` (the OpenStack object form, plus
the string / array forms cloud-init tolerates) are applied to the resolved
default user, merged with the cloud-config top-level `ssh_authorized_keys`.

**Cleanup hook.** No-op. Same rationale as NoCloud.

---

## EC2

A stub for future AWS use. Returns `NotApplicable` today. Tracked under
[RFC 0008](../../rfcs/0008-platform-native-provisioner-coexistence.md).

---

## Override (CLI)

`egs-service run --user-data <path>` constructs an `OverrideDataSource`
that bypasses discovery entirely. The instance id is either the
`--instance-id` flag or `cli-override-<8-hex>`. Useful for `--dry-run`
and for re-running the agent against a synthetic payload without
touching the real datasource.
