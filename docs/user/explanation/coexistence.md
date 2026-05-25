# Coexistence with platform agents

Most clouds ship their own Windows guest agent. Where the agent stands on each:

## Azure

Two Microsoft components own the Azure wireserver channel: the Provisioning
Agent, which runs during OOBE — it applies the computer name, admin user, and
RDP from `ovf-env.xml`, writes `C:\AzureData\CustomData.bin`, sends the first
wireserver Ready signal, and exits — and the long-running Windows Guest Agent,
which handles heartbeats, extensions, and telemetry. That channel is never idle
on a Microsoft Windows image, and a second writer on it risks confusing the
fabric.

So the agent stays off it completely: it never contacts the wireserver, never
reports as a Microsoft component, and never re-applies the computer name, admin
user, or RDP — the Provisioning Agent already did those. It reads
`CustomData.bin`, queries instance metadata at `169.254.169.254`, applies the
cloud-config, and deletes `CustomData.bin` on success.

Run the agent on Azure *without* the Provisioning Agent and the fabric never
sees its Ready signal, so the VM times out. The agent is a complement to the PA,
not a replacement for it.

## AWS

EC2Launch v2 is convenience tooling, not a fabric requirement — AWS marks a VM
healthy once its system and network checks pass. Replacing EC2Launch means
taking over sysprep, volume initialization, and password handling. The last one
is visible: without EC2Launch the console's "Get Password" stops working, so
deliver keys through cloud-config instead. The EC2 datasource is a stub today.

## GCP

GCP's guest agent isn't required, but OS Login, project-metadata SSH keys, and
hostname propagation degrade without it. Keep the GCP guest agent in the image
and let this agent handle user-data. The GCP datasource isn't implemented yet.

## OpenStack

The agent takes the slot cloudbase-init held on OpenStack. The ConfigDrive
datasource reads the `config-2` ISO that OpenStack produces.

## Hyper-V (eryph)

No native agent — the agent is it. eryph-zero attaches a `config-2` drive and
the agent reports back over Hyper-V KVP.

## An existing cloudbase-init

Don't run the agent alongside cloudbase-init on the same guest — both would try
to consume the same datasource. Pick one: replace cbi with `egs-service` on
eryph, or keep cbi on other clouds until the gaps in
[Windows cloud-init status](windows-cloud-init-status.md) are closed. Exactly one
cloud-init runtime should be active on a guest.
