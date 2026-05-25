# Provisioning agent

eryph guest-services ships two capabilities in one Windows binary
(`egs-service`):

- **Remote access** — `egs-tool` reaches a guest over the Hyper-V vsock
  for remote shell, command exec, file transfer, and pty. SSH is just
  the transport; no network, no listener on a TCP port.
- **Provisioning** — a cloud-init-style first-boot agent. It reads a
  cloud-init datasource, applies the user-data, network-config, and
  meta-data, and reports progress back to the host over Hyper-V KVP.

These docs cover provisioning. Where it makes sense the agent behaves
the way [cloud-init](https://docs.cloud-init.io/) does — same stages,
module names, frequencies, and semaphore layout — adapted to Windows.

## Where the agent runs

- **Hyper-V standalone.** `egs-service` gives you remote access to a
  plain Hyper-V VM. Provisioning is in the same binary but optional: no
  datasource means the agent logs *No data source available* once and
  exits. Remote access keeps working.
- **eryph.** Every catlet boots with `egs-service`. It finds the
  `config-2` ConfigDrive eryph-zero attaches and applies the cloud-config
  fodder. State and reporting flow through Hyper-V KVP.
- **Other clouds (work in progress).** A cloudbase-init replacement for
  Hyper-V / OpenStack / Azure guests outside eryph. The Azure datasource
  is implemented and verified; several cloud-init features are not yet
  done. See [Windows cloud-init status](explanation/windows-cloud-init-status.md)
  before relying on it outside eryph.

## Tutorials

- [Getting started](tutorial/getting-started.md) — install, dry-run, watch the stages.
- [Your first catlet with cloud-config](tutorial/first-catlet-with-cloud-config.md) — the eryph workflow end to end.

## How-to

- [Write a cloud-config](howto/write-a-cloud-config.md)
- [Use multipart user-data](howto/use-multipart-user-data.md)
- [Use `#include` URLs](howto/use-include-url.md)
- [Run shell scripts (PowerShell / cmd)](howto/run-shell-scripts.md)
- [Configure networking](howto/configure-networking.md)
- [Reset and re-run provisioning](howto/reset-and-rerun.md)
- [Collect logs for support](howto/collect-logs-for-support.md)

## Reference

- [Modules](reference/modules.md)
- [Datasources](reference/datasources.md)
- [CLI](reference/cli.md)
- [User-data formats](reference/user-data-formats.md)
- [Stages](reference/stages.md)
- [Frequencies](reference/frequencies.md)
- [Settings (`egs-provisioning.json`)](reference/settings.md)

## Explanation

- [Architecture](explanation/architecture.md)
- [Differences from cloud-init](explanation/differences-from-cloud-init.md)
- [Differences from cloudbase-init](explanation/differences-from-cloudbase-init.md)
- [Coexistence with platform agents](explanation/coexistence.md)
- [Windows cloud-init status](explanation/windows-cloud-init-status.md)

Per-decision rationale and deferred work live in the
[RFCs](../rfcs/README.md). The RFC index status column is the canonical
source for implemented vs. drafted vs. deferred.
