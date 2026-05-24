# Cloud-init real-world fixtures

These cloud-config fixtures pin our YAML deserializer's PyYAML SafeLoader /
YAML 1.1 compatibility against artifacts produced by cloud-init upstream
(plus one hand-authored fixture for a gap upstream does not cover). They
are consumed by `RealWorldCloudInitFixtureTests` in
`test/Eryph.GuestServices.CloudConfig.Yaml.Tests/`.

## License & attribution

The upstream fixtures below are copied verbatim from the
[canonical/cloud-init](https://github.com/canonical/cloud-init) repository,
which is dual-licensed under Apache-2.0 and GPLv3. We use them under the
**Apache License 2.0** (see `LICENSE-Apache-2.0` in this directory), the
same license that covers the `Eryph.GuestServices.CloudConfig` model
assembly. Copyright remains with Canonical Ltd. and the cloud-init authors.

All upstream fixtures were captured at cloud-init commit
`9c7d85d82c16c2379b1f004623c9d43507f31d3e` (branch `main`).

## Fixtures

| File | Source | Why it lives here |
| --- | --- | --- |
| `cloud-config-master-example.yaml` | [`doc/examples/cloud-config.txt`](https://github.com/canonical/cloud-init/blob/9c7d85d82c16c2379b1f004623c9d43507f31d3e/doc/examples/cloud-config.txt) | The canonical, exhaustive example. Uses `package_update: false`, `resize_rootfs: True`, `chpasswd: { expire: False }`, `ssh_pwauth: True`, `disable_ec2_metadata: true`, `ssh_redirect_user: true` — capitalised YAML 1.2 bool forms that YamlDotNet's default parser already handles, plus the documented `manage_etc_hosts` / `resize_rootfs` bool\|string unions. Exercises the whole acknowledged-key surface in one document. |
| `cloud-config-apt.yaml` | [`doc/examples/cloud-config-apt.txt`](https://github.com/canonical/cloud-init/blob/9c7d85d82c16c2379b1f004623c9d43507f31d3e/doc/examples/cloud-config-apt.txt) | `apt_pipelining: False` documents the bool member of cloud-init's `bool \| "none" \| int` 3-way union (which we keep as `object?` — see `CloudConfig.AptPipelining`). Also mixes `preserve_sources_list: true` (lowercase) with capitalised bools, confirming both casings round-trip. |
| `cloud-config-update-packages.yaml` | [`doc/examples/cloud-config-update-packages.txt`](https://github.com/canonical/cloud-init/blob/9c7d85d82c16c2379b1f004623c9d43507f31d3e/doc/examples/cloud-config-update-packages.txt) | Minimal `package_upgrade: true` — the smallest real upstream bool fixture, useful as a focused regression. |
| `eryph-yaml11-lowercase-bool-tokens.yaml` | **eryph contribution** (not copied) | Cloud-init's example corpus canonicalises to `True`/`False`, so it never exercises the lowercase YAML 1.1 tokens (`yes`/`no`/`on`/`off`/`y`/`n`) that PyYAML SafeLoader still resolves to bool. This fixture closes that gap: every value is a lowercase YAML 1.1 bool token across `bool?`, `BoolOrString`, nested-record, and list-entry targets. Authored by the eryph guest-services project; no upstream copyright. |
