# Real-world OpenStack ConfigDrive fixture

Unlike the hand-authored `test/fixtures/configdrive/` tree (a *representative*
config-2 layout), this fixture is a **real config-2 drive emitted by nova**,
captured from a single-node DevStack (`stable/2025.1`) booting an instance with
`FORCE_CONFIG_DRIVE=True` / `config_drive_format = iso9660`. The capture was a
one-off, out-of-tree procedure (the raw capture is intentionally not committed —
it contained the sensitive values stripped below). This sanitized tree is the
checked-in artifact; it is consumed by `RealWorldOpenStackConfigDriveTests` in
`test/Eryph.GuestServices.Provisioning.Tests/`.

The point of pinning real nova output (not synthetic JSON) is to lock
`ConfigDriveDataSource` against the exact field set, nesting and types a real
OpenStack producer writes — see the project's real-world-fixtures rule.

## Sanitization

The tree was hand-stripped before committing. **Structure, field names, file
set and JSON types are preserved verbatim** as nova wrote them; only sensitive
*values* were replaced:

| Field | Replaced with |
| --- | --- |
| `meta_data.json` → `uuid` | `facade00-0000-4000-8000-000000000001` |
| `meta_data.json` → `hostname` | `egs-fixture.novalocal` |
| `meta_data.json` → `public_keys` (every value) | `ssh-ed25519 AAAAC3NzaC1lZDI1NTE5FIXTUREKEY egs@fixture` |
| `meta_data.json` → `name` | `egs-fixture` |
| `meta_data.json` → `project_id` | `00000000000000000000000000000000` |
| `meta_data.json` → `keys[].data` | same placeholder key as `public_keys` |
| MAC / IP / neutron-UUID values in `network_data.json` | left as captured (throwaway DevStack IDs); not asserted |
| `meta_data.json` → `random_seed` and `admin_pass` | **deleted** — see note below |

`random_seed` is a base64 entropy blob and `admin_pass` is a generated
password; nova writes both into `meta_data.json`. `ConfigDriveDataSource`
consumes neither, so both keys are removed from the fixture: they add no
coverage, and a large opaque base64 string / a `*_pass` field trip content
scanners. Every other field nova emitted (`launch_index`, `keys`, `devices`, …)
is kept verbatim.

`availability_zone` (`nova`) and all other non-sensitive values are kept as
captured.

## Layout

Only the newest version directory cloud-init can select (`2018-08-27`, the
newest in `OS_VERSIONS`) is retained — that is the directory
`ConfigDriveDataSource` actually reads. Verified against cloud-init `main`
(`helpers/openstack.py`, `_find_working_version`): the version walk tops out at
`OS_ROCKY = "2018-08-27"`, so newer real dirs (`2020-10-14`, `2025-04-04`,
`latest`) are never selected by date and are dropped here, along with the EC2
files (not consumed by the ConfigDrive datasource).

```
openstack/2018-08-27/meta_data.json      (required)
openstack/2018-08-27/user_data           (#cloud-config; carried as bytes)
openstack/2018-08-27/network_data.json   (as emitted by nova)
openstack/2018-08-27/vendor_data.json    (as emitted by nova; empty object)
openstack/2018-08-27/vendor_data2.json   (as emitted by nova; not consumed)
```
