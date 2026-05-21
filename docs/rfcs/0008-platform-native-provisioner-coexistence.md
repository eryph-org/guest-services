# RFC 0008 — Platform-native provisioner coexistence

Status: Draft

## Problem

Most clouds ship their own Windows guest agent. We coexist with them, we don't replace them. This RFC codifies the per-platform behavior matrix.

## What each platform agent does

### Azure
- **Microsoft Provisioning Agent (PA)** runs during oobeSystem.
- Reads `ovf-env.xml` from the Azure ConfigDrive.
- Applies: ComputerName, admin user + password, RDP enable.
- Writes user-data to `C:\AzureData\CustomData.bin`, ejects the ConfigDrive.
- Re-enables `cloudbase-init` (or our agent) via `SetupComplete2.cmd`.
- Reports provisioning state to the Azure wireserver (so the VM shows Ready in the portal).

### AWS
- **EC2Launch v2** (modern) or **EC2Config** (legacy) — the AWS Windows initializer.
- Sets ComputerName from instance metadata.
- Generates admin password, encrypts with the user's keypair, surfaces via AWS console / API.
- Sets up drive letters for ephemeral storage.
- Executes user-data (`#!/cmd` or `<powershell>` markers — AWS-specific format).
- Runs once per instance unless re-enabled.

### OpenStack
- **CloudbaseInit was the canonical agent** historically. With our replacement we ARE the OpenStack agent.

### Hyper-V (eryph on-prem)
- No native agent. We do everything.

## Eryph stance

**We coexist with the platform's native provisioner. Each datasource encodes "what the native agent already did, don't redo":**

### Azure
- Datasource returns user-data from `CustomData.bin`.
- Returns **null** for hostname (PA set it from ovf-env.xml).
- Returns **null** for admin user/password (PA created the user).
- Vendor-data slot can carry per-platform defaults if Azure ever publishes them.
- Reporting: emit our events to KVP + Log; also emit to Azure wireserver (RFC 0006) so the Azure portal knows we're done.

### AWS
- Datasource returns user-data from IMDS.
- Returns **null** for hostname (EC2Launch sets it).
- Returns **null** for password (EC2Launch generates + encrypts).
- EC2Launch handles its own user-data execution. **Coordination question**: does AWS run BOTH EC2Launch's user-data executor AND our agent? If yes, user-data may run twice. Resolution: only one of them processes the user-data. Recommend: **EC2Launch handles AWS-specific user-data formats (`<powershell>` etc.)**; **our agent handles `#cloud-config`**. The two formats are syntactically distinct.

### OpenStack
- Datasource returns full payload. We're the only agent.

### Hyper-V
- KVP datasource or ConfigDrive. We're the only agent.

## Open questions

- AWS user-data dual processing — confirm with eryph use case. Some customers may want only one path. Configurable in `egs-provisioning.json`?
- Azure wireserver "Ready" signal — what if reporting fails? Azure may mark the VM as "Failed" after a timeout. RFC 0006 handles this.
- Does Azure PA ever fail to set the ComputerName? If yes, our `SetHostnameModule` would no-op (datasource returned null) and the VM has the wrong name. Defensive: datasource could populate Hostname from VmId-based lookup as a fallback.
