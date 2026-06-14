# RFCs

Design notes for the eryph guest provisioning agent. Each RFC captures a deferred decision: the problem, what cloud-init does, what cloudbase-init does, what we tentatively plan, and what's open.

## Status legend

- **Draft** — under discussion, not implemented
- **Accepted** — direction agreed, implementation pending
- **Implemented** — landed in code; RFC kept for context
- **Superseded** — replaced by a later RFC (link to successor)
- **Dropped** — decided not to pursue (with reason)

## Index

| # | Title | Status |
|---|---|---|
| [0001](0001-vendor-data-merge-policy.md) | Vendor-data merge policy | Draft |
| [0002](0002-network-config-v1-v2-application.md) | Network-config v1/v2 application on Windows | Implemented |
| [0003](0003-module-frequencies.md) | Module frequencies (per-instance / per-boot / per-once) | Implemented |
| [0004](0004-datasource-readiness-timeout.md) | Datasource readiness — probe timeout, retry backoff | Implemented |
| [0005](0005-datasource-cleanup-hook.md) | Datasource cleanup hook timing | Implemented |
| [0006](0006-multi-handler-reporting-cloud-backends.md) | Multi-handler reporting — Azure / AWS cloud backends | Draft |
| [0007](0007-scripts-per-frequency-edge-cases.md) | `scripts/per-*` semantics edge cases | Implemented |
| [0008](0008-platform-native-provisioner-coexistence.md) | Platform-native provisioner coexistence (Azure PA, EC2Launch v2) | Draft |
| [0009](0009-module-list-split.md) | Module-list split (`cloud_init_modules` / `cloud_config_modules` / `cloud_final_modules`) | Implemented |
| [0010](0010-semaphore-design.md) | Semaphore design — single JSON vs per-module files | Implemented |
| [0011](0011-jinja2-templating.md) | Jinja2 templating in user-data | Draft |
| [0012](0012-part-handler.md) | Part-handler (custom code in user-data) | Draft |
| [0013](0013-boothook-execution.md) | Boothook execution | Draft |
| [0014](0014-azure-datasource.md) | Azure datasource (probe + CustomData.bin + IMDS + ovf-env) | Implemented |
| [0015](0015-set-timezone-module.md) | `timezone` cloud-config module on Windows | Implemented |
| [0016](0016-ntp-module.md) | `ntp` cloud-config module on Windows | Implemented |
| [0017](0017-licensing-module.md) | `license` cloud-config module (slmgr / AVMA / KMS / rearm) | Implemented |
| [0018](0018-ssh-module.md) | Windows OpenSSH daemon configuration (`SshModule`) | Implemented |
| [0019](0019-windows-update-module.md) | `windows_update` cloud-config module | Draft |
| [0020](0020-winget-module.md) | `winget` cloud-config module | Draft |
| [0021](0021-chocolatey-module.md) | `chocolatey` cloud-config module | Draft |
| [0022](0022-chef-module.md) | `chef` cloud-config module (orchestrator bootstrap) | Draft |
| [0023](0023-extend-volumes-module.md) | `growpart` cloud-config module on Windows | Implemented |
| [0024](0024-power-state-module.md) | `power_state` cloud-config module | Implemented |
| [0025](0025-dsc-module.md) | `dsc` cloud-config module (DSCv3, retires the `windsc` gene workarounds) | Draft |
| [0026](0026-winrm-listener-deferred.md) | WinRM listener / certificate auth (deferred) | Draft |
| [0027](0027-set-locale-module.md) | `locale` / `keyboard` cloud-config module on Windows | Implemented |
| [0028](0028-linux-keys-module.md) | Acknowledged-but-no-op top-level keys — Info-log Linux / deferred keys via `CloudConfigSerializer` | Implemented |
| [0029](0029-secure-config-bootstrap-handshake.md) | Secure config delivery via bootstrap handshake | Draft |
| [0031](0031-cloud-init-compatible-kvp-status.md) | Cloud-init-compatible provisioning status over Hyper-V KVP | Accepted |

## Authoring

When promoting a `Draft` to `Accepted`, add a "Decision" section. When implemented, change status, append "Implemented in commit `<sha>`" and leave the RFC for future reference. Don't delete RFCs after implementation — they explain the *why* that the code can't.
