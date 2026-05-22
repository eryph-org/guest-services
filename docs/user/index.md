# Provisioning agent — user documentation

The provisioning agent is the cloud-config processor embedded in
`egs-service` (the Windows guest binary that also serves SSH-over-Hyper-V).
It consumes a cloud-init datasource on first boot, applies the user-data,
network-config, and meta-data fields, and reports progress back to the
host. Wherever sensible it behaves the way
[cloud-init](https://docs.cloud-init.io/) does — same stages, same
module names, same frequencies, same semaphore layout — adapted to a
Windows guest.

## Pick your use case

The agent ships in three shapes. The docs below cover all three, but each
page tells you when a section is specific to one of them.

### (a) Hyper-V standalone — provisioning is optional

`egs-service` provides userless SSH access to a plain Hyper-V VM. The
provisioning agent is in the same binary but you never have to use it.
If no cloud-init datasource is present, the agent logs *No data source
available* once and exits cleanly. SSH-over-Hyper-V keeps working.

### (b) Eryph — the standard path

In every eryph catlet, `egs-service` runs on first boot, finds the
cloud-init ConfigDrive (`config-2`) that eryph-zero attached to the VM,
and applies the cloud-config fodder. State and reporting flow through
Hyper-V KVP so the host can watch the run.

### (c) General-purpose Windows cloud-init — work in progress

A drop-in cloudbase-init replacement for Hyper-V / OpenStack / Azure
guests outside eryph. The core machinery is in place (see
[the WIP status page](explanation/windows-cloud-init-status.md)) but
several cloud-init features are not yet implemented. Not production-ready
outside eryph today.

## Documentation map (Diátaxis)

### Tutorials — learn by doing

- [Getting started](tutorial/getting-started.md) — install, dry-run, see the agent process a sample.
- [Your first catlet with cloud-config](tutorial/first-catlet-with-cloud-config.md) — eryph workflow end-to-end.

### How-to — task-oriented recipes

- [Write a cloud-config](howto/write-a-cloud-config.md)
- [Use multipart user-data](howto/use-multipart-user-data.md)
- [Use `#include` URLs](howto/use-include-url.md)
- [Run shell scripts (PowerShell / cmd)](howto/run-shell-scripts.md)
- [Configure networking](howto/configure-networking.md)
- [Reset and re-run provisioning](howto/reset-and-rerun.md)
- [Collect logs for support](howto/collect-logs-for-support.md)

### Reference — flat, lookup-style

- [Modules](reference/modules.md)
- [Datasources](reference/datasources.md)
- [CLI](reference/cli.md)
- [User-data formats](reference/user-data-formats.md)
- [Stages](reference/stages.md)
- [Frequencies](reference/frequencies.md)
- [Settings (`egs-provisioning.json`)](reference/settings.md)

### Explanation — concepts and design

- [Architecture](explanation/architecture.md)
- [Differences from cloud-init](explanation/differences-from-cloud-init.md)
- [Differences from cloudbase-init](explanation/differences-from-cloudbase-init.md)
- [Coexistence with platform agents](explanation/coexistence.md)
- [Windows cloud-init status (WIP)](explanation/windows-cloud-init-status.md)

## Where to look for *why*

This guide tells you *what* the agent does and *how* to use it. The
*why* — every deferred design decision, every deliberate divergence from
cloud-init — lives in the [RFCs](../rfcs/README.md). Each reference
page links the relevant RFC.
