# RFC 0014 — Azure datasource

Status: Draft

## Problem

The provisioning agent must consume Azure-injected first-boot data alongside Microsoft's Provisioning Agent (PA). The current `AzureDataSource` is a stub: it detects an Azure environment via a single registry key and returns `WaitForReady` until `CustomData.bin` materialises, then `NotApplicable`. We need a real v1 that:

1. Probes Azure reliably (matches cbi's detection: registry key + chassis asset tag).
2. Reads the data sources Azure actually exposes after PA has run.
3. Coexists with PA per RFC 0008 — never signals Ready to the wireserver.
4. Surfaces user-data through the existing byte-clean pipeline.
5. Defers CustomData decryption (RFC 0015) without blocking the v1 path.

## What cloud-init / cloudbase-init do

Cloudbase-init's `AzureService` (and cloud-init's `DataSourceAzure`):

- Detect Azure via either `HKLM\SOFTWARE\Microsoft\Windows Azure\VmId` OR the SMBIOS chassis asset tag `7783-7084-3265-9085-8269-3286-77` (the well-known "Azure chassis tag").
- Read the Azure ConfigDrive (`ovf-env.xml`) on first boot — extracts `ProvisioningSection/LinuxProvisioningConfigurationSet` or `WindowsProvisioningConfigurationSet` (HostName, AdminPassword, CustomData, SSHKeys, DisableSshPasswordAuthentication).
- Query IMDS at `http://169.254.169.254/metadata/instance?api-version=2021-02-01` with the mandatory `Metadata: true` header for ongoing instance metadata (vmId, location, subscriptionId, vm size, network).
- On Windows, post-PA the ConfigDrive is usually ejected; user-data is persisted to `C:\AzureData\CustomData.bin` (base64-decoded if the ovf-env CustomData element was base64-encoded).
- **POST** to the wireserver `/machine?comp=health` Ready endpoint to signal provisioning success. **On Windows the channel is owned by Microsoft components throughout the VM's lifetime — see "Coexistence" below — cloudbase-init does NOT signal Ready when PA is present.**

## Detection (v1)

Two independent signals; either is sufficient and they are belt-and-suspenders:

1. **Registry key:** `HKLM\SOFTWARE\Microsoft\Windows Azure\VmId` value present and non-empty.
   - Already implemented by `PlatformProbes.IsRunningOnAzure()`; reuse it (do not duplicate the read).
2. **Chassis asset tag:** SMBIOS `Win32_SystemEnclosure.SMBIOSAssetTag` equals `7783-7084-3265-9085-8269-3286-77`.
   - Used by Azure-internal Linux images and as a fallback when the registry has not yet been populated (very early boot).

Either-match wins. Both miss → `NotApplicable`.

## Data sources (v1)

Three inputs, tried in this order:

### (a) `C:\AzureData\CustomData.bin`

Written by PA from the ovf-env.xml `<CustomData>` element after PA's first-boot work. This is the canonical post-PA user-data location on Windows.

**CustomData is NOT encrypted** — neither at the API level nor in ovf-env. PA base64-decodes the ovf-env element once and writes the resulting bytes verbatim. We expose `UserData` as those bytes; the downstream pipeline handles whatever the user submitted (gzipped multipart MIME, `#cloud-config`, `#include`, etc. — `DataSourceResult.UserData` is `byte[]`). MS docs explicitly warn "do not store sensitive data in custom data" — confirmation that it's not protected at rest.

Verified across cloud-init `helpers/azure.py` (`_parse_property("CustomData", decode_base64=True)`), WALinuxAgent `protocol/ovfenv.py` (`findtext(conf_set, "CustomData", …)` — no cert lookup), and cloudbase-init `azureservice.py` (`get_user_data()` reads `CUSTOM_DATA_FILENAME` with `open(rb).read()`, comment "Don't decode to retain compatibility"). See [research/azure-customdata-encryption.md](../research/azure-customdata-encryption.md) for per-source citations.

The OSProfile `<CertificateThumbprint>` machinery in ovf-env wraps **AdminPassword** and **LinuxConfigurationSet/SSH** key payloads — not CustomData. Those fields are already applied by PA during OOBE; we don't re-process them.

### (b) IMDS — instance metadata service

`GET http://169.254.169.254/metadata/instance?api-version=2021-02-01`

Mandatory headers:
- `Metadata: true` (Azure rejects requests without it to prevent SSRF abuse).
- `Accept: application/json` (defensive — some load balancers default to XML otherwise).

Client policy:
- 5-second per-attempt timeout.
- One retry on transient error (5xx, `HttpRequestException`, timeout). Two attempts total.
- No auth token (IMDS instance metadata is unauthenticated; only managed-identity tokens need auth).

Used for: `compute.vmId` (becomes `InstanceId`), `compute.name` (Hostname fallback), `compute.location` (Region), `compute.vmSize` (InstanceType), `compute.zone` / `compute.platformFaultDomain` (AvailabilityZone). The full payload is flattened into `MetaData`.

### (c) ovf-env.xml on the ConfigDrive

Post-PA on a normal Azure boot the ConfigDrive is unmounted and `ovf-env.xml` is unavailable. But:
- On a re-provision or a boot where PA failed early, it may still be mounted (label `cidata`-like or CD-ROM with `ovf-env.xml` at the root).
- We keep the parser so we can extract `HostName`, `CustomData` (encrypted blob), and `CertificateThumbprint` if it is there. Cross-references RFC 0015 (decryption).

XML namespace: `http://schemas.microsoft.com/windowsazure`.

Targeted elements (under `ProvisioningSection`):
- `LinuxProvisioningConfigurationSet` → `HostName`, `UserName`, `UserPassword`, `DisableSshPasswordAuthentication`, `CustomData`, `SSH/PublicKeys/PublicKey/Fingerprint`.
- `WindowsProvisioningConfigurationSet` → `ComputerName`, `AdminPassword`, `CustomData`, `EnableAutomaticUpdates`.

The v1 parser only needs HostName/ComputerName and CustomData. Other fields are noted for RFC 0015.

## Boot ordering

PA runs in `oobeSystem`. By the time our service starts (per RFC 0008, gated by `SetupComplete2.cmd` or our own service start), PA has:

- Applied ComputerName, admin user, RDP enable.
- Decoded ovf-env CustomData → `C:\AzureData\CustomData.bin` (still encrypted if a CertificateThumbprint was set).
- Ejected the ConfigDrive.
- Signalled Ready to the wireserver.

So the v1 read path is almost always (a) + (b). (c) is a fallback for edge cases.

## Priority

Azure = 10 in `DataSourceLocator`. Already documented in `ProvisioningContainerBuilder` and unchanged. NoCloud (30) and ConfigDrive (40) defensively decline when `PlatformProbes.IsRunningOnAzure()` is true.

## Coexistence with PA + WinGA (HARD CONSTRAINT)

Cross-reference: [RFC 0008](0008-platform-native-provisioner-coexistence.md) and [research/azure-wireserver-analysis.md](../research/azure-wireserver-analysis.md).

On a Microsoft-Windows Azure image, **two** Microsoft components own the wireserver channel — at no point is it idle:

- **PA** runs during `oobeSystem`, applies ovf-env, sends the **first** `<State>Ready</State>` POST, then exits. Not a long-running service.
- **WinGA** (`WindowsAzureGuestAgent.exe`) is the long-running Windows service. It owns the **ongoing** goal-state polling loop, heartbeat health POSTs, extension installation, OSProfile cert sync, and telemetry — indefinitely after PA exits.

**We MUST NOT POST to `/machine?comp=health` at all.** PA owns the first Ready; WinGA owns every Ready after that. A second writer from us risks fabric confusion regardless of which MS component is "current". Our role is strictly:

- Read CustomData.bin (do not delete it; PA / WinGA may inspect their own state).
- Query IMDS for live instance metadata.
- Skip hostname / admin user / RDP / wireserver / telemetry / extensions — all owned by PA + WinGA.

The default v1 stance is **skip the wireserver entirely**. We do not even probe `?comp=versions` — IMDS plus registry / chassis-tag detection is sufficient to identify Azure. See the research note for the full per-endpoint inventory and rationale.

The `AzureDataSource.OnCompletedAsync` hook may safely delete `C:\AzureData\CustomData.bin` after our pipeline has consumed it (mirrors cbi's `AzureCustomDataService.provisioning_completed()`). Deferring this is fine for v1; tracked as a TODO in the implementation.

## DataSourceResult shape

| Field | v1 value |
|---|---|
| `SourceName` | `"Azure"` |
| `InstanceId` | IMDS `compute.vmId`; if IMDS fails, the registry VmId; if both miss the source returns `Failed`. |
| `Hostname` | ovf-env HostName/ComputerName if present, else IMDS `compute.name`. Per RFC 0008 we still emit it so handlers downstream can log / verify — PA having already applied it makes the cloud-config `set_hostname` a no-op when values match. |
| `UserData` | Raw bytes of `C:\AzureData\CustomData.bin` if present, else null. **Byte-exact** — never round-trip through `ReadAllText`. |
| `VendorData` | null (Azure has no vendor-data concept). |
| `MetaData` | Flattened `compute.*` from IMDS. |
| `PlatformMetadata` | CloudName="azure", Platform="azure", Subplatform="customdata", LocalHostname=Hostname, Region=`compute.location`, AvailabilityZone=`compute.zone`, InstanceType=`compute.vmSize`. |
| `NetworkConfig` | null (PA configured the NIC; IMDS network section is for diagnostics only in v1). |

## Not deferred — clarifications from research

- **CustomData decryption: not a thing.** Originally listed as deferred to RFC 0015. We later verified that CustomData is base64-only plaintext at every layer (cloud-init, WALinuxAgent, cloudbase-init, MS docs). PA already base64-decoded it before writing `CustomData.bin`. There is no envelope to decrypt; the v1 datasource as designed already produces the correct bytes. RFC 0015 is dropped.
- **AdminPassword / SSH key decryption: explicitly out of scope.** Those fields ARE PKCS#7-encrypted in ovf-env, and PA decrypts them with the OSProfile cert during OOBE. We never re-process them.
- ovf-env-supplied SSH public-key fingerprints (Linux-shape; rarely seen on Windows) — not implemented; PA handles SSH key material on the rare Linux Azure path before we'd see it.

## Open questions

- **`api-version`** — 2021-02-01 is widely supported and ships the `compute.userData` element. The v1 implementation does NOT read IMDS `userData` (it is a Linux-flavoured alternative to ovf-env CustomData that PA does not populate on Windows). If we ever want to support managed-identity-based scenarios, bump to 2021-12-13.
- **Chassis asset tag read on Linux** — out of scope, this agent is Windows-only.
- **CustomData.bin retention** — RFC 0005 says cleanup goes through `OnCompletedAsync`. PA does not appear to re-read CustomData.bin after first boot, but confirm before enabling the delete.

## References

- [research/azure-wireserver-analysis.md](../research/azure-wireserver-analysis.md) — per-endpoint inventory, citation-backed dissection of cloud-init / cloudbase-init / WALinuxAgent / MS docs.
