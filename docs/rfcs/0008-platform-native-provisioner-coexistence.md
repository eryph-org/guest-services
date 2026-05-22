# RFC 0008 — Platform-native provisioner coexistence

Status: Draft

## Problem

Most clouds ship their own Windows guest agent. The relationship varies — some clouds *require* an agent handshake to consider provisioning successful; some treat the agent as a convenience. We need an explicit per-platform stance.

## The "is the agent required?" axis

This is the dimension that matters most. Clouds split into three groups:

| Platform | Native agent | Platform requires the handshake? | Provisioning success signal |
|---|---|---|---|
| **Azure** | Microsoft Provisioning Agent (PA) + Windows Guest Agent (WinGA, `WindowsAzureGuestAgent.exe`) | **YES — hard requirement** | POST to wireserver `/machine?comp=health` with `Ready`. PA sends the first Ready during OOBE then exits; WinGA owns the ongoing heartbeat indefinitely. Fabric tears the VM down if the first Ready is missing. |
| **AWS** | EC2Launch v2 (or EC2Config legacy) | **NO — entirely optional** | Hypervisor + network reachability checks only. AWS doesn't care if anything inside the guest talks back. |
| **GCP** | GCP Guest Agent | **SOFT** — agent is strongly expected (SSH keys, hostname, accounts flow through it), but no hard fabric-level boot handshake | Health checks similar to AWS; agent absence is tolerated but breaks several console features. |
| **OpenStack / Hyper-V (eryph)** | None | n/a | We are the agent. |

## What each platform agent does (and what we lose if we replace it)

### Azure — Microsoft Provisioning Agent (PA) + Windows Guest Agent (WinGA)
**Required, must coexist.** Microsoft splits provisioning across two components:

**PA** runs during oobeSystem. It:
- Reads `ovf-env.xml` from the Azure ConfigDrive.
- Applies: ComputerName, admin user + password, RDP enable.
- Writes user-data to `C:\AzureData\CustomData.bin`, ejects the ConfigDrive.
- Imports the OSProfile decryption cert into `Cert:\LocalMachine\My`.
- Re-enables follow-on agents (cloudbase-init / our agent) via `SetupComplete2.cmd`.
- **Critically:** POSTs the **first** `Ready` to the wireserver `/machine?comp=health`. If this doesn't happen within the timeout, the Azure fabric controller tears the VM down or flags it as failed.
- Exits when done — PA is not a long-running service.

**WinGA** (`WindowsAzureGuestAgent.exe`) is the long-running Windows service. After PA exits it:
- Polls goal-state and POSTs ongoing health heartbeats (the channel is **never idle**).
- Installs and updates VM agent extensions.
- Keeps OSProfile certs synced in `Cert:\LocalMachine\My` (re-imports them if deleted).
- Posts telemetry as `WALinuxAgent`/`WindowsAzureGuestAgent`.

**Conclusion: we MUST coexist with both. We never POST to `/machine?comp=health`, never call any wireserver endpoint, never emit telemetry as a Microsoft agent.** See [RFC 0014](0014-azure-datasource.md) and [research/azure-wireserver-analysis.md](../research/azure-wireserver-analysis.md) for the per-endpoint inventory.

### AWS — EC2Launch v2
**Optional, can replace.** EC2Launch v2 is convenience tooling, not a fabric requirement. If we ship an AMI with no AWS agent at all and only our agent, EC2 will report the instance healthy as soon as the system + network status checks pass. There's no equivalent to Azure's wireserver handshake.

**What we lose by removing EC2Launch v2:**
- **Sysprep integration** — `ec2launch sysprep` is the canonical AMI-generalization workflow on AWS. We need our own sysprep approach (eryph likely already has one for cross-platform image baking).
- **Volume initialization** — bringing additional EBS volumes online, drive-letter assignment, formatting. Our SystemInit-equivalent (Local stage) `ExtendVolumesHandler` + a hypothetical `DriveLetterHandler` would cover this.
- **Password generation + encrypted surfacing** — EC2Launch generates a random admin password, encrypts it with the user's keypair, and surfaces it via "Get Password" in the AWS console. If we replace EC2Launch, eryph must either (a) ship keys via cloud-config (no random password generation), or (b) implement the same encrypt-with-keypair flow ourselves. **Recommended: rely on cloud-config-supplied users/keys; skip random-password generation.**
- **CloudWatch agent bootstrap helpers** — minor, only matters if eryph customers use CloudWatch.
- **EC2 console features** that depend on the agent: "Get Password", serial console, EC2 Instance Connect for Windows flows.

**ENA / NVMe drivers** are shipped via separate AWS PV driver packages, not EC2Launch — we keep those regardless of EC2Launch presence.

**Conclusion: for eryph cross-platform consistency, REPLACE EC2Launch v2 in eryph-built AMIs by default. Document what we absorb.** Customers who need "Get Password" or EC2 Instance Connect can opt back into EC2Launch via the eryph AMI build configuration; our agent then coexists (the EC2 datasource returns null hostname/password, same pattern as Azure).

### GCP — Guest Agent
**Soft requirement, recommend coexistence.** GCP's guest agent isn't a fabric-required handshake like Azure, but several GCP features depend on it:
- SSH key management via OS Login / project-metadata keys
- Hostname propagation from instance metadata
- Account provisioning (sudo-equivalent groups via OS Login)
- Telemetry / monitoring agent integration

GCP can boot a VM without it, but the experience degrades sharply. If eryph targets GCP, recommendation: **coexist with the GCP Guest Agent**, similar to the Azure pattern. The GCP datasource returns null for hostname / users / SSH keys (the agent handled them); we process user-data only.

### OpenStack — historical cloudbase-init
**We replace it.** Eryph IS the OpenStack agent.

### Hyper-V (eryph on-prem)
**No native agent.** We're it.

## Refined per-platform behavior matrix

| Platform | Coexistence stance | Datasource omits | Datasource provides |
|---|---|---|---|
| Azure | **Coexist with PA + WinGA (mandatory)** | hostname, admin user/password (PA already did them); ALL wireserver endpoints (PA + WinGA own them indefinitely); telemetry as a Microsoft agent | user-data from `CustomData.bin`, stable instance-id from `HKLM\SOFTWARE\Microsoft\Windows Azure\VmId` |
| AWS | **Replace EC2Launch v2 by default** (opt-in to coexist for "Get Password") | (default) nothing — we handle everything | user-data from IMDS, instance-id from IMDS, network-config from IMDS |
| GCP | **Coexist with Guest Agent** | hostname, users, SSH keys | user-data from metadata service, instance-id from metadata, network-config from metadata |
| OpenStack | Replace | nothing | full payload from ConfigDrive |
| Hyper-V | Replace | nothing | full payload from ConfigDrive / KVP |

## Open questions

- **Cross-platform AMI baking**: if eryph ships one Windows image that works on Azure + AWS + GCP, does the agent detect platform at runtime via datasource probing only, or do we bake platform-specific config?
- **AWS user-data format**: AWS user-data can be wrapped in `<powershell>...</powershell>` or `<script>...</script>` tags — formats EC2Launch parses. If we replace EC2Launch, we either (a) honor those formats in our user-data pipeline as content-type handlers, or (b) require eryph users to ship cloud-config only. Lean (b); document.
- **AWS "Get Password" replacement** — if customers want randomly generated admin passwords on AWS without EC2Launch, we need our own keypair-encrypted-password surfacing channel. Out of scope for v1; tracked here.
- **GCP Guest Agent ordering** — does our service need to wait for the GCP agent's first-boot work to complete, similar to Azure PA? Probably yes for SSH key sync. Investigate before any GCP support work.
- **Azure fabric timeout** — historically 60 min for the Ready signal. Our 10-min `WaitForReady` cap in RFC 0004 leaves plenty of margin, but worth confirming the current Azure value.
