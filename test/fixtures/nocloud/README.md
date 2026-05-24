# NoCloud datasource fixtures

These fixtures pin `NoCloudDataSource`'s meta-data parsing against the shape
real-world NoCloud `cidata` seeds use. They are consumed by
`NoCloudDataSourceTests` in `test/Eryph.GuestServices.Provisioning.Tests/`.

## Reference

NoCloud meta-data is a YAML document with an open schema. The keys and the
nested-map / block-scalar forms used here are taken from cloud-init's
documented NoCloud datasource reference:

- <https://cloudinit.readthedocs.io/en/latest/reference/datasources/nocloud.html>
- <https://cloudinit.readthedocs.io/en/latest/reference/network-config-format-eni.html> (the `network-interfaces` block-scalar form)

cloud-init does not publish a single canonical NoCloud meta-data file we can
copy verbatim (the docs show fragments), so this fixture is hand-authored to
be representative of the documented shape. Authored by the eryph
guest-services project; no upstream copyright.

## Fixtures

| File | Why it lives here |
| --- | --- |
| `meta-data` | A representative NoCloud meta-data document: the flat `instance-id` / `local-hostname` scalars cloud-init always emits, a nested `public-keys:` map (per the NoCloud docs), and a `network-interfaces: |` block scalar. Pins that the parser flattens scalars, preserves structured values as serialized text instead of dropping them, and does not crash on a block scalar. |
