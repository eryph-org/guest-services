# Differences from cloudbase-init

The agent fills the same role as cloudbase-init (cbi) on a Windows guest: a
cloud-init-compatible runtime that reads a datasource, runs modules and scripts,
and reports to the host. It accepts cbi-shaped payloads and goes further toward
full cloud-init compatibility.

| Area | This agent (vs cloudbase-init) |
| --- | --- |
| Cloud-config | Accepts the full `#cloud-config` schema, multipart MIME, and `#include`. cbi supports a subset. |
| Locale, keyboard, timezone, NTP, licensing | First-class cloud-config keys (`locale`, `keyboard`, `timezone`, `ntp`, `license`). On cbi you script these or drive them from cbi's own config file. |
| SSH | `SshModule` configures the OS OpenSSH daemon — merges `authorized_keys`, manages host keys, writes an `sshd_config` drop-in. |
| Stages | cloud-init's `Local` / `Network` / `Config` / `Final`, not cbi's plugin phases. |
| network-config | v1 and v2 both accepted. cbi takes only the older OpenStack shape. |
| Frequencies | per-instance, per-boot, and per-once, with semaphores. cbi has per-instance only. |
| Script dispatch | By filename extension, with a shebang fallback when there's no extension. cbi goes by filename only. |
| Parts without a filename | Logged and run as PowerShell. cbi drops them. |
| Random passwords | Not supported — rejected at validate, skipped at runtime. cbi posts the generated value to the metadata service; Windows guests on the clouds eryph targets have no reliably-captured console channel to return it. Set an explicit password. |
| Azure wireserver | The agent never sends the Ready signal — the Provisioning Agent and Windows Guest Agent own that channel. Running without the PA on Azure will time out. See [Coexistence](coexistence.md). |
| Packaging | One binary (`egs-service`) over a shared library, not a Python interpreter and plugin chain. The same library backs the service and the CLI. |

`growpart` refreshes disk geometry before extending a volume, so it picks up a
host-side VHD resize. Reboot-and-continue (exit code `1003`), the Hyper-V KVP
reporting protocol, and POSIX-permission-to-NTFS-ACL translation all behave as
they do under cbi.

For what's ready outside eryph and what's still missing, see
[Windows cloud-init status](windows-cloud-init-status.md).
