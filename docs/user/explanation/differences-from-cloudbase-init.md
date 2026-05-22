# Differences from cloudbase-init

The agent fills the same niche as cloudbase-init on a Windows guest: a
cloud-init-compatible runtime that consumes datasources, runs modules
and scripts, signals back to the host. Where it diverges:

## Summary table

| Topic | cloudbase-init | Agent | Why |
| --- | --- | --- | --- |
| Script dispatch | Filename-led only; **shebangs ignored** | Filename-led primary + **shebang fallback** when no usable extension | Filename matches cbi (so eryph fodder Just Works); shebang fallback recovers cloud-init-shaped payloads that omit a filename. |
| Multipart parts without `filename=` | Silently dropped | Accepted (script-kind detector logs a warning and best-effort dispatches to PowerShell) | A hand-written cloud-config might omit the filename; we don't want a silent drop. |
| Multipart without close delimiter (`--boundary--`) | May fail / drop the last part | Tolerated — last open part is flushed | Real eryph fodder ships without the close delimiter; dropping the last script is worse. |
| Azure ovf-env without `CertificateThumbprint` on `CustomData` | Some versions panic | Accepted — CustomData is read as plain bytes regardless | CustomData is **not** encrypted at any layer; the thumbprint applies to `AdminPassword` / SSH keys (which PA handles, not us). See [RFC 0014](../../rfcs/0014-azure-datasource.md). |
| User-data byte handling | Historically round-tripped through UTF-8 text | **Bytes are bytes** end-to-end | Gzipped multipart user-data (eryph-zero ships it that way) is not valid UTF-8; `ReadAllText` would silently corrupt it. |
| Stages | OOBE / pre-networking / main / post-sysprep — Python plugin chain | Cloud-init's Local / Network / Config / Final shape | Cloud-init mental model; aligns with the [cloud-init-fidelity memory rule](../../../C:/Users/fwagner/.claude/projects/F--source-repos-eryph-guest-services/memory/feedback_cloud_init_fidelity.md). |
| Module frequencies | Implicit per-instance; no per-boot | Explicit per-instance / per-boot / per-once with semaphores | [RFC 0003](../../rfcs/0003-module-frequencies.md). |
| Network-config | OpenStack-style only | cloud-init network-config v1 and v2 | [RFC 0002](../../rfcs/0002-network-config-v1-v2-application.md). |
| Reporting | Hyper-V KVP only (via the eryph patch) | Hyper-V KVP + Log handlers, multi-handler dispatch built in for future Azure / AWS / webhook backends | [RFC 0006](../../rfcs/0006-multi-handler-reporting-cloud-backends.md). |
| Process / packaging | One Python interpreter + plugin chain | Library + service: `Eryph.GuestServices.Provisioning` is a library; `egs-service.exe` embeds it; the same library hosts the CLI | Lets the same library back the long-running service and the operator CLI without duplication. |
| Exit code 1003 reboot-and-continue | Supported | Supported (same semantics) | Direct compat. |
| Azure wireserver Ready POST | Sent by cbi when running solo; suppressed when Microsoft PA is present | **Never sent** (under any circumstance) | PA + WinGA own that channel indefinitely; see [Coexistence](coexistence.md) and [RFC 0008](../../rfcs/0008-platform-native-provisioner-coexistence.md). |
| Random password surface | Available via plugin chain | `chpasswd.users[].type: RANDOM` generates 16-char password; **secret reporting channel not yet implemented** | The orchestrator can't yet harvest the random value programmatically. Out-of-band only. |

## Things that match deliberately

- Filename-led script dispatch as the **primary** rule (this is the
  whole point — eryph fodder lives in cbi's bug-shape).
- Hyper-V KVP reporting protocol.
- Reboot-and-continue exit code 1003.
- POSIX permissions → NTFS ACL translation (owner / group→Users /
  others→Everyone; SYSTEM and Administrators always retain FullControl).
- `OnCompletedAsync` fires only on full provisioning success (mirrors
  cbi's `provisioning_completed`).
- Azure `CustomData.bin` cleanup on success.

## Where we deliberately stay closer to cloud-init than cbi

- Stage names (`Local`, `Network`, `Config`, `Final`) rather than cbi's
  plugin-chain phases.
- Network-config v1 and v2 (cbi only supports the older OpenStack
  shape).
- Per-boot and per-once frequencies (cbi has only per-instance).
- Semaphore layout (one file per module-frequency, mirroring cloud-init).
- Vendor-data slot in the datasource result, even though the merge
  policy is deferred.

## Reading more

See [the WIP status page](windows-cloud-init-status.md) for what's
actually production-ready outside eryph and what's still in progress.
