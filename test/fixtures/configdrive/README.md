# ConfigDrive datasource fixtures

These fixtures pin `ConfigDriveDataSource`'s OpenStack ConfigDrive reading
against the shape real-world config-2 ISOs use. They are consumed by
`ConfigDriveDataSourceTests` in `test/Eryph.GuestServices.Provisioning.Tests/`.

## Reference

OpenStack ConfigDrive lays metadata out under `openstack/<version>/`, where
`<version>` is a dated metadata-API version (e.g. `2018-08-27`) and `latest`
is a symlink to the newest. cloud-init's reader walks the dated versions
newest-first and uses the first one present, falling back to `latest`
(`cloudinit/sources/helpers/openstack.py`, `_find_working_version`). The
`meta_data.json` schema (`uuid`, `hostname`, `name`, `availability_zone`,
`public_keys`, ...) is documented here:

- <https://docs.openstack.org/nova/latest/user/metadata.html#metadata-openstack-format>
- <https://cloudinit.readthedocs.io/en/latest/reference/datasources/configdrive.html>

OpenStack does not publish a single canonical `meta_data.json` we can copy
verbatim, so this fixture is hand-authored to be representative of the
documented format. Authored by the eryph guest-services project; no upstream
copyright.

## Fixtures

| File | Why it lives here |
| --- | --- |
| `openstack/2018-08-27/meta_data.json` | A representative `meta_data.json` placed under a *dated* version directory (no `latest`). Pins that the version-walk picks the dated directory newest-first (Finding 19) and that the nested `public_keys` object is surfaced as `DataSourceResult.SshPublicKeys` (Finding 20), not flattened to a raw JSON string. |
