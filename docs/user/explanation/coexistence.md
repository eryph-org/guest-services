# Coexistence with platform agents

Most public clouds ship their own Windows guest agent. The provisioning
agent's stance on each is summarised below. The full rationale and
per-platform inventory lives in
[RFC 0008](../../rfcs/0008-platform-native-provisioner-coexistence.md)
and [RFC 0014](../../rfcs/0014-azure-datasource.md).

## Azure — coexist with PA + WinGA (mandatory)

Two Microsoft components own the Azure wireserver channel:

- **Provisioning Agent (PA)** — runs during `oobeSystem`, applies
  ComputerName / admin user / RDP from `ovf-env.xml`, writes
  `C:\AzureData\CustomData.bin`, **sends the first wireserver
  `Ready`**, then exits.
- **WinGA (`WindowsAzureGuestAgent.exe`)** — long-running service.
  Heartbeats, extension installation, OSProfile cert sync, telemetry.

The wireserver channel is **never idle** on a Microsoft-Windows image.
A second writer risks fabric confusion regardless of which MS component
is "current".

**Hard rules for our agent:**

- Never POST to the wireserver `/machine?comp=health` Ready endpoint.
- Never call any other wireserver endpoint.
- Never emit telemetry under a Microsoft agent name.
- Never re-apply ComputerName / admin user / RDP — PA did them.

**What we do:** read `CustomData.bin` (raw bytes), query IMDS at
`169.254.169.254` for live instance metadata, apply the cloud-config
fodder.

**Cleanup:** delete `CustomData.bin` after successful provisioning
(mirrors cloudbase-init's `AzureCustomDataService.provisioning_completed`).

## AWS — replace EC2Launch v2 by default

EC2Launch v2 is convenience tooling, not a fabric requirement. AWS
considers a VM healthy as soon as system + network checks pass —
there's no equivalent to Azure's wireserver handshake.

**What we absorb if we replace EC2Launch:** sysprep integration,
volume initialization, password generation + console-surfacing. The
last one is the one customers notice: without EC2Launch, the
"Get Password" feature in the EC2 console stops working. For that,
ship the keys via cloud-config.

**Status:** stub. The EC2 datasource is registered but returns
`NotApplicable`.

## GCP — coexist with Guest Agent (soft)

GCP's guest agent isn't a fabric requirement but several features
degrade without it (OS Login, project-metadata SSH keys, hostname
propagation). Recommendation: keep the GCP Guest Agent in the image
and let our agent handle user-data only. The GCP datasource is not
yet implemented.

## OpenStack — replace

Our agent fills the slot historically owned by cloudbase-init on
OpenStack. The `ConfigDrive` datasource reads the `config-2` ISO
layout OpenStack produces.

## Hyper-V (eryph) — no native agent

`config-2` from eryph-zero, KVP for reporting. We're it.

## Pre-existing cloudbase-init

If a Windows image already has cloudbase-init installed and our agent
is added alongside, both will probably attempt to consume the same
datasource. **Don't do this.** Use one or the other:

- On eryph: replace cbi with `egs-service`.
- On other clouds: keep cbi for now, or replace it explicitly and
  watch the [Windows cloud-init status](windows-cloud-init-status.md)
  page for what's still missing.

There's no in-tree mechanism for cbi-and-agent coexistence; the
implicit assumption is that on any given guest, exactly one cloud-init
runtime is active.
