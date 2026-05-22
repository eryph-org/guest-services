# Datasources

The agent locates exactly one datasource at the start of each run. The
locator probes every registered source, lowest `Priority` value first,
and returns the first one that says `Ready`. If a source says
`WaitForReady`, the locator backs off (1s → 60s, exponential) and tries
again, sharing a global wall-clock budget with every other source
(default 5 minutes). See [RFC 0004](../../rfcs/0004-datasource-readiness-timeout.md).

If no source becomes ready within the budget, the run exits cleanly
with `NoDataSource` — no provisioning happens, no failure is reported.

| Source | Priority | Requires network |
| --- | --- | --- |
| `Azure` | 10 | yes |
| `EC2` | 20 | yes (stub) |
| `NoCloud` | 30 | no |
| `ConfigDrive` | 40 | no |

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

**Layout expected:**

| File | Required | Used for |
| --- | --- | --- |
| `openstack/latest/meta_data.json` | yes | `uuid` (instance id), `hostname` / `name`, `availability_zone` |
| `openstack/latest/user_data` | no | Raw user-data bytes |
| `openstack/latest/vendor_data.json` | no | Vendor data (parsed; merge deferred) |
| `openstack/latest/network_data.json` | no | network-config (best-effort YAML parse — JSON form is not v1 supported) |

Bytes are read raw, same as NoCloud.

**Cleanup hook.** No-op. Same rationale as NoCloud.

---

## EC2

A stub for future AWS use cases. Returns `NotApplicable` today. Out of
scope for use case (b) eryph; tracked under
[RFC 0008](../../rfcs/0008-platform-native-provisioner-coexistence.md).

---

## Override (CLI)

`egs-service run --user-data <path>` constructs an `OverrideDataSource`
that bypasses discovery entirely. The instance id is either the
`--instance-id` flag or `cli-override-<8-hex>`. Useful for `--dry-run`
and for re-running the agent against a synthetic payload without
touching the real datasource.
