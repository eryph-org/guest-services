# Differences from cloudbase-init

The agent fills the same niche as cloudbase-init (cbi) on a Windows
guest: a cloud-init-compatible runtime that reads a datasource, runs
modules and scripts, and reports back to the host. It is broadly
compatible with cbi-shaped payloads, and goes further toward full
cloud-init compatibility in several places. This page lists where it
differs in practice.

## Broader cloud-init compatibility

cbi supports a subset of cloud-config. This agent accepts the full
`#cloud-config` schema, multipart MIME, and `#include` / `#include-once`.
cloud-config keys that cbi has no equivalent for are first-class here:

- **Locale / keyboard / timezone.** `locale:`, `keyboard.layout:`, and
  `timezone:` are cloud-config keys. On cbi you script these through
  runcmd. A system-locale change requests a reboot only when the value
  actually changed.
- **NTP.** The `ntp:` block configures the Windows Time service. The
  `RealTimeIsUniversal` registry value is opt-in (`ntp.real_time_clock_utc`,
  default leaves the Windows default) — cbi always writes it. Hyper-V
  guests already match the host's local-time RTC; rewriting it every run
  is a needless reboot trigger. Operators on non-UTC hosts (KVM, Xen) can
  opt in.
- **Licensing.** A cloud-config `license:` block with auto-detected
  AVMA/KMS (built-in key table through Server 2025), eval-gated rearm
  (`slmgr /rearm` only on evaluation editions), and an Azure skip
  (Windows on Azure activates natively). cbi drives licensing from its
  own config file.
- **SSH.** `SshModule` configures the OS-level Win32-OpenSSH daemon:
  merges `authorized_keys`, writes host keys (or operator-supplied
  `ssh_keys`), and writes an `sshd_config` drop-in for
  `PasswordAuthentication` / `DenyUsers`.

## Scripts and user-data

- **Filename-led dispatch with a shebang fallback.** Like cbi, the agent
  picks the runner from the script's filename extension (cbi ignores
  shebangs entirely). Unlike cbi, when there is no usable extension the
  agent falls back to the shebang (`#ps1`, `#ps1_sysnative`) — recovering
  cloud-init-shaped payloads that omit a filename.
- **Parts without `filename=` are not dropped.** cbi silently drops a
  multipart part that has no `filename=`. The agent logs a warning and
  best-effort dispatches it to PowerShell instead of dropping it.
- **User-data is handled as bytes end to end.** Gzipped multipart
  user-data (eryph-zero ships it that way) is not valid UTF-8; reading it
  as text would corrupt it. The agent never round-trips user-data through
  a string.
- **Exit code 1003 reboot-and-continue** works with the same semantics
  as cbi.

## Platform behavior

- **Azure: never POSTs the wireserver Ready signal.** cbi sends it when
  running solo and suppresses it under Microsoft's Provisioning Agent
  (PA). This agent never sends it under any circumstance — PA and the
  Windows Guest Agent own that channel. Running the agent without PA on
  Azure means the fabric will time out. See [Coexistence](coexistence.md).
- **Azure CustomData is read as plain bytes.** A missing
  `CertificateThumbprint` on `CustomData` in `ovf-env` does not matter —
  CustomData is not encrypted at any layer. The thumbprint applies to
  `AdminPassword` / SSH keys, which PA handles. Some cbi versions choke on
  this.
- **Volume extension uses the WSM path only.** `growpart` calls
  `MSFT_Disk.Update()` before enumerating partitions, fixing the GPT
  secondary header after a host-side VHD resize. cbi's VDS path has a
  rounding bug and does not refresh disks; the agent sidesteps both.
- **Random passwords are not supported.** `type: RANDOM`, the
  `chpasswd.list` `R`/`RANDOM` tokens, and password-less entries are
  rejected by `validate` and warn-skipped at runtime. cbi posts the
  generated value to the metadata service; this agent doesn't, and
  Windows guests have no console channel reliably captured across the
  clouds eryph targets, so a generated password could never be retrieved.
  Set an explicit password.

## Stages and frequencies (closer to cloud-init than cbi)

- **Stage names follow cloud-init** — `Local`, `Network`, `Config`,
  `Final` — not cbi's OOBE / pre-networking / main / post-sysprep plugin
  phases. Same cloud-init mental model.
- **Network-config v1 and v2** are both accepted. cbi supports only the
  older OpenStack shape.
- **Per-boot and per-once frequencies** exist, with semaphores (one file
  per module-frequency, mirroring cloud-init). cbi has only per-instance.

## Packaging

cbi is one Python interpreter plus a plugin chain. The agent is a library
(`Eryph.GuestServices.Provisioning`) hosted by the `egs-service` binary —
the same library backs the long-running service and the operator CLI.

## Things that match deliberately

- Filename-led script dispatch as the primary rule (eryph fodder lives in
  cbi's filename-mandatory shape).
- Hyper-V KVP reporting protocol.
- Reboot-and-continue exit code 1003.
- POSIX permissions → NTFS ACL translation (owner / group→Users /
  others→Everyone; SYSTEM and Administrators always retain FullControl).
- The completion hook fires only on full provisioning success.
- Azure `CustomData.bin` cleanup on success.

## Reading more

See [Windows cloud-init status](windows-cloud-init-status.md) for what's
ready outside eryph and what's still missing.
