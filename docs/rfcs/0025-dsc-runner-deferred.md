# RFC 0025 — DSC runner (DEFERRED)

Status: Draft

## Problem

Operators arriving from cloudbase-init may expect to push a PowerShell
Desired-State-Configuration (DSC) MOF at first boot — "compile the
config on a build server, drop the MOF in user-data, let the guest
apply it via `Start-DscConfiguration`". Cloud-config has no native
`dsc:` key, but cbi exposed one; users with cbi-era fodder may want
parity.

## What cloud-init does

Nothing. DSC is a Windows-PowerShell concept; cloud-init never grew a
module for it.

## What cloudbase-init does

`cloudbaseinit/plugins/windows/dsc.py` — accepts a DSC configuration
(MOF file path, or PowerShell script that compiles to a MOF), copies
it to the local DSC store, and invokes `Start-DscConfiguration` so the
guest converges to the declared state.

Source:
<https://github.com/cloudbase/cloudbase-init/blob/master/cloudbaseinit/plugins/windows/dsc.py>

## What surprised us about the cbi plugin

The plugin is heavier than it sounds: it doesn't just hand the MOF to
`Start-DscConfiguration`. It also wires the Local Configuration
Manager (LCM) — pulls down a pull-server-style configuration if the
operator provides a URL, registers certificates for secure-string
decryption, and accepts a PowerShell snippet that COMPILES the MOF on
the guest. That last point is the gnarly one: it implies a build-time
PowerShell environment in the guest, which fights cloud-config's "no
state before first boot" assumption. Most operators we've seen used
the simpler "MOF blob in user-data" path.

## Why deferred

PowerShell DSC v1 is in maintenance only. Microsoft has shifted focus
to DSCv3 (still experimental as of late 2026), and Azure customers
running DSC workloads are being routed to Azure Machine Configuration
/ Guest Configuration, which is delivered by the Azure agent rather
than by cloud-init/cbi-style modules. Real operator demand for DSCv1
on cloud Windows is shrinking; the cbi DSC plugin is rarely-used
relative to runcmd / Chef / Puppet.

We will revisit when one of the following becomes true:

1. **DSCv3 stabilises** with a stable Windows-PowerShell story. The
   schema and the CLI surface (`dsc.exe`) are different enough from
   v1 that an `egs-service` module written today would need a rewrite
   to track v3.
2. **A concrete consumer shows up** with a DSCv1 use case that cannot
   be served by `chef:` (RFC 0022), `runcmd`, or `scripts/per-instance`.
   "I want to compile a MOF in user-data and apply it" is a workflow
   we believe is now niche enough that the carrying cost of a
   purpose-built module exceeds the benefit. Operators with that
   workflow can drop a `.ps1` into `scripts/per-instance/` today and
   get the same result without a custom module.

What would change our minds, in priority order:

- A reproducible report from an eryph user that "we run a fleet on
  cbi today and our DSC pipeline doesn't fit in runcmd."
- Microsoft promoting DSCv3 to GA with first-class Windows-PowerShell
  integration AND a clear story for cloud-config-style bootstrapping.

## Open questions (for the revisit)

- If we ship DSC support eventually, v1-or-v3? Probably v3 — anything
  built today against v1 is dead-end work. Plan to skip v1 and adopt
  v3 once it stabilises.
- Pull-server scenarios (LCM points at an HTTPS endpoint, guest fetches
  config + modules from there). Big surface area; probably out of
  scope even in a future implementation — operators with pull servers
  already have their own bootstrap tooling.
- MOF decryption certificates — DSCv1 requires the cert to be present
  in the local cert store before the MOF is applied. That ordering
  constraint (cert → MOF → apply) needs explicit module orchestration.

## Cross-references

- [RFC 0022](0022-chef-module.md) — Chef is the modern replacement for
  many of the use cases DSCv1 historically served on Windows
  (declarative config, idempotent convergence, central node registry).
- [RFC 0011](0011-jinja2-templating.md), [RFC 0012](0012-part-handler.md),
  [RFC 0013](0013-boothook-execution.md) — fellow Drafts deferred for
  lack of concrete consumer demand.
