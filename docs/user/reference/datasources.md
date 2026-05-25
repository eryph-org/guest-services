# Datasources

The agent picks one datasource at the start of a run. It probes the registered
sources in priority order and uses the first one that has data. A source that
isn't ready yet (Azure during OOBE, say) is retried with a growing backoff
(1s up to 60s), sharing one overall budget across all sources — 15 minutes by
default. If nothing becomes ready in that time, the run exits cleanly without
provisioning; it isn't treated as a failure.

| Source | Priority | Needs network |
| --- | --- | --- |
| Azure | 10 | yes |
| EC2 | 20 | yes (stub) |
| NoCloud | 30 | no |
| ConfigDrive | 40 | no |

By default all sources are probed in this order. To restrict or reorder them,
set `dataSources.dataSourceList` in [settings](settings.md) — the sources you
name are probed in that order and the rest are ignored. `egs-service run
--user-data` bypasses discovery entirely and uses the file you pass.

## Azure

Detected by the `HKLM\SOFTWARE\Microsoft\Windows Azure\VmId` registry value or
the Azure SMBIOS asset tag. The agent reads, in order: `C:\AzureData\CustomData.bin`
(written by Microsoft's Provisioning Agent during OOBE — raw bytes, not encrypted),
the instance metadata service at `169.254.169.254` for instance details, and
`ovf-env.xml` from a still-mounted ConfigDrive as a fallback. The instance id
comes from IMDS, then the registry; the hostname from ovf-env or IMDS (the PA has
usually already applied it).

The agent never touches the Azure wireserver. The Provisioning Agent and the
Windows Guest Agent own that channel and the hostname, admin user, and RDP
setup — the agent stays off it entirely. See
[Coexistence](../explanation/coexistence.md).

On success the agent deletes `CustomData.bin` (and its directory if empty). A
cleanup failure is logged and the run still succeeds.

## NoCloud

Detected by a mounted volume labelled `cidata`. On Azure the source declines —
that disk belongs to the Provisioning Agent.

| File | Required | Used for |
| --- | --- | --- |
| `meta-data` | yes | `instance-id`, `local-hostname`, and any other keys |
| `user-data` | no | the user-data payload |
| `vendor-data` | no | vendor data (read, not applied) |
| `network-config` | no | network-config v1 or v2 |

`user-data` and `vendor-data` are read as raw bytes — real payloads are often
gzipped multipart MIME, which isn't valid text. A `seedfrom` entry in
`meta-data` pointing at a `file://` or `http(s)://` base fetches the seed's
`meta-data` and `user-data` from there.

The agent doesn't eject the volume on success, so `egs-service reset` can read
the same payload again.

## ConfigDrive

Detected by a mounted volume labelled `config-2`; declines on Azure for the same
reason as NoCloud. The agent walks `openstack/<version>/` newest-first across the
dated OpenStack versions (`2018-08-27` down to `2012-08-10`) and reads the first
that has a `meta_data.json`, falling back to `openstack/latest/`. ISOs without
the `latest` link are still found.

| File | Required | Used for |
| --- | --- | --- |
| `openstack/<version>/meta_data.json` | yes | `uuid` (instance id), hostname, availability zone, `public_keys` |
| `openstack/<version>/user_data` | no | the user-data payload |
| `openstack/<version>/vendor_data.json` | no | vendor data (read, not applied) |
| `openstack/<version>/network_data.json` | no | network-config (best-effort) |

`public_keys` (the OpenStack object form, or the string/array forms cloud-init
accepts) are applied to the default user, alongside the cloud-config
`ssh_authorized_keys`. The volume isn't ejected on success.

## EC2

A stub for future AWS support — it reports no data today.

## CLI override

`egs-service run --user-data <path>` reads a payload from disk and skips
datasource discovery. The instance id comes from `--instance-id` or a generated
`cli-override-<hex>` value. Useful with `--dry-run` and for re-running against a
test payload.
