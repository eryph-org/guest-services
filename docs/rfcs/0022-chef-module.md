# RFC 0022 — `chef` cloud-config module

Status: Draft

## Problem

Operators who manage their fleet with Chef expect a cloud-config `chef:`
block to bootstrap the chef-client on first boot. eryph base catlets
historically relied on cloudbase-init to do… nothing — cbi has no Chef
plugin, so chef bootstrap on Windows already meant "write a long
`runcmd` that fetches the omnibus installer, runs it, drops `client.rb`,
calls `chef-client`". Now that egs-service replaces cbi, the
`chef:` key is still silently dropped. For cloud-init compatibility we
need a real module: cloud-init has shipped `cc_chef` for years, the
schema is large, and operators coming from a mixed Linux/Windows fleet
expect the same YAML to work everywhere.

## What cloud-init does

`cc_chef` installs the chef-client (omnibus installer or distro
packages), writes `/etc/chef/client.rb` from the supplied keys, drops
`validation.pem`, then optionally runs `chef-client` once so the node
registers with the Chef server in the same boot. Default frequency:
per-instance.

Cloud-init reference:
<https://cloudinit.readthedocs.io/en/latest/reference/modules.html#chef>

## What cloudbase-init does

No Chef plugin. Real eryph fodder that wants Chef today scripts the
entire installer dance from `runcmd`.

## Schema

Mirror cloud-init's `chef:` block 1:1 — the field list is large but
cheap to carry on the POCO and avoids round-trip loss:

```yaml
chef:
  install_type: omnibus            # omnibus | packages
  omnibus_url: https://omnitruck.chef.io/install.sh
  omnibus_version: 18.4.2
  omnibus_url_retries: 5
  force_install: false
  server_url: https://chef.example.org/organizations/eryph
  validation_name: eryph-validator
  validation_cert: |
    -----BEGIN RSA PRIVATE KEY-----
    ...
  validation_key: /etc/chef/validation.pem
  node_name: catlet-001
  environment: _default
  run_list:
    - "role[windows-base]"
    - "recipe[iis]"
  client_key: /etc/chef/client.pem
  file_cache_path: /var/chef/cache
  file_backup_path: /var/chef/backup
  pid_file: /var/run/chef-client.pid
  encrypted_data_bag_secret: /etc/chef/encrypted_data_bag_secret
  chef_license: accept
  log_level: info
```

## What Windows needs

- Omnibus installer URL has a Windows-amd64 variant
  (`https://omnitruck.chef.io/install.ps1` → `chef-client.msi`).
- `client.rb` lives at `C:\chef\client.rb`; `validation.pem` at
  `C:\chef\validation.pem`. Unix-style paths in the cloud-config
  (`/etc/chef/...`) are translated via the existing
  `IWindowsOs.TranslateUnixPath` helper, matching how
  `WriteFilesModule` already handles `/var/log/...`.
- `chef-client.exe` runs as LocalSystem under the service account — no
  user impersonation. MSI install is silent
  (`msiexec /i chef-client.msi /qn`).

## Tentative direction

### POCO sketch

```csharp
public sealed record ChefConfig
{
    public string? InstallType { get; init; }            // "omnibus" | "packages"
    public string? OmnibusUrl { get; init; }
    public string? OmnibusVersion { get; init; }
    public int? OmnibusUrlRetries { get; init; }
    public bool? ForceInstall { get; init; }
    public string? ServerUrl { get; init; }
    public string? ValidationName { get; init; }
    public string? ValidationCert { get; init; }         // inline PEM
    public string? ValidationKey { get; init; }          // path
    public string? NodeName { get; init; }
    public string? Environment { get; init; }
    public IReadOnlyList<string>? RunList { get; init; }
    public string? ClientKey { get; init; }
    public string? FileCachePath { get; init; }
    public string? FileBackupPath { get; init; }
    public string? PidFile { get; init; }
    public string? EncryptedDataBagSecret { get; init; }
    public string? ChefLicense { get; init; }
    public string? LogLevel { get; init; }
}
```

### Module

- `Eryph.GuestServices.Provisioning.Modules.ChefModule`
  (`[Stage(Stage.Final, Order = ..., Frequency = ModuleFrequency.PerInstance)]`).
- Final stage because the installer is heavy and the first
  `chef-client` run wants the rest of the catlet in steady state.
- New `IWindowsOs.InstallChefOmnibusAsync(url, version, retries, ...)`
  hides the download + MSI invocation behind the `IWindowsOs` boundary
  so `DryRunWindowsOs` can intercept and `WindowsOs` can use
  `BackgroundDownloadAsync` + `RunArgvCommandAsync`.
- `client.rb` is rendered from the POCO via a small text composer —
  not a full ERB renderer, just key/value lines (`chef_server_url`,
  `validation_client_name`, `node_name`, `log_level`, `environment`).
- `chef_license accept` is required for Chef 15+; the module refuses
  to run `chef-client` when missing and logs a clear error.
- After install + `client.rb` write + `validation.pem` write, the
  module calls `chef-client -j first-boot.json` once with the
  `run_list`. Non-zero exit → module Failed, do not block other Final
  modules.

## Open questions

- **Puppet / Ansible / Salt are deliberately separate follow-up RFCs
  after Chef ships.** Each has its own installer (puppet-agent MSI,
  ansible-pull, salt-minion MSI) and config-file dance. Folding them
  into 0022 would balloon the schema; better to ship Chef end-to-end,
  then copy the same shape per orchestrator.
- Should `force_install: true` re-run the MSI even when chef-client
  is already present? cloud-init says yes; cost is a slow reinstall
  on every per-instance run after an `egs-tool reset`. Tentative: yes,
  mirror cloud-init.
- Inline `validation_cert` vs path-only `validation_key`: cloud-init
  accepts both. We write the inline cert to the `validation_key` path
  before kicking the client; document the precedence.

## Cross-references

- [RFC 0009](0009-module-list-split.md) — operators can disable Chef
  via `disabledModules: [ChefModule]` once their nodes are bootstrapped.
- [RFC 0010](0010-semaphore-design.md) — per-instance frequency means
  one chef-client first-run per catlet identity.
