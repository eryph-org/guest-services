# RFC 0025 — `dsc` cloud-config module (DSCv3)

Status: Draft

## Problem

Eryph operators run DSC workloads in catlets today via the `dbosoft/windsc`
gene, which works around two DSCv1 quirks at boot time:

1. **NuGet bootstrap** so that subsequent `Install-Module` calls for DSC
   resources succeed (`Get-PackageProvider NuGet -ForceBootstrap`).
2. **Self-signed certificate** in `Cert:\LocalMachine\My` so that MOFs
   containing `PSCredential` fields can encrypt their secrets at
   compile time and the LCM can decrypt them at apply time.

Both workarounds are pure DSCv1 baggage. They exist because the catlet
has no pre-installed PowerShellGet provider chain and no per-instance
crypto material; the operator's actual goal is "apply a declarative
config at first boot". With **DSCv3 generally available and backwards
compatible with v1 resources**, neither workaround is needed.

The plan: replace the gene-side workarounds with a first-class `dsc:`
cloud-config module that targets DSCv3, runs DSCv1 resources through
v3's compatibility layer, and removes both the NuGet bootstrap and the
self-signed-cert dance.

## What cloud-init does

Nothing. DSC has always been Windows-PowerShell; cloud-init never grew a
module. Closest parallel on Linux is `cc_puppet` /
`cc_ansible` — same problem (declarative idempotent convergence at first
boot), different tooling.

## What cloudbase-init does

`cloudbaseinit/plugins/windows/dsc.py` — DSCv1 plugin:

- Accepts a MOF path, an inline MOF blob, or a PowerShell snippet that
  COMPILES a MOF on the guest.
- Configures the Local Configuration Manager (LCM) — optionally points
  at a pull-server endpoint.
- Imports certificates for `PSCredential` decryption.
- Invokes `Start-DscConfiguration` to converge.

Source:
<https://github.com/cloudbase/cloudbase-init/blob/master/cloudbaseinit/plugins/windows/dsc.py>

Heavier than it sounds — the in-guest MOF compilation path requires a
PowerShell build environment in the guest, which is exactly what
DSCv3 eliminates.

## What changed with DSCv3

DSCv3 (the `microsoft/DSC` project — `dsc.exe`, native cross-platform
binary) replaces the DSCv1 model:

- **No MOF compilation.** Configurations are YAML documents (or JSON);
  the resource manifest model evaluates them directly.
- **No NuGet / PowerShellGet bootstrap.** DSCv3 resources are either
  built-in to `dsc.exe`, ship as standalone executables, or are
  discovered via PowerShell modules — but the dependency on
  PSGallery + NuGet provider for the runner itself is gone.
- **No mandatory secret-encryption certificate.** Credential handling
  moves to provider-specific schemes (env vars, secret stores,
  whatever the resource declares); the legacy MOF-encryption dance is
  not in the v3 model.
- **Backwards-compatible with DSCv1 resources** via the
  `PSDesiredStateConfiguration` adapter. Operators with existing v1
  resources keep them working.
- **CLI**: `dsc config set --file <yaml>` to apply, `dsc config test`
  to dry-run, `dsc config get` to inspect current state.

Reference: <https://learn.microsoft.com/en-us/powershell/dsc/overview>.

## Tentative direction

### Schema (mirror cbi's shape but DSCv3-native)

```yaml
dsc:
  install: auto                       # auto | skip — auto installs DSCv3 if missing
  install_source: winget              # winget | github_release | path
  install_version: latest             # optional pin (e.g. '3.1.0')
  configurations:
    - name: baseline
      document: |                     # inline YAML config document
        $schema: https://aka.ms/dsc/schemas/v3/bundled/config/document.json
        resources:
          - name: Set Time Zone
            type: Microsoft.Windows/WindowsPowerShell
            properties:
              ...
    - name: from-url
      document_url: https://example.com/configs/web-server.dsc.config.yaml
      checksum: 'sha256:...'          # optional integrity check
    - name: from-file
      document_path: '/ProgramData/eryph-e2e/configs/baseline.dsc.yaml'
  apply: set                          # set | test | get (matches dsc.exe verbs)
  continue_on_error: false            # stop at first failed configuration
```

The legacy DSCv1 `mof_url`, `mof_content`, `compile_script` and `lcm_*`
keys from cbi are NOT carried forward — they have no DSCv3 analogue
and operators with v1 MOFs should run them through the
`PSDesiredStateConfiguration` v3 adapter (or call the cbi-style script
via `runcmd`).

### Module

`Eryph.GuestServices.Provisioning.Modules.DscModule` — Stage = Final,
Frequency = PerInstance. Runs after `RuncmdModule` so an operator
runcmd can stage configuration files first if needed.

### Installation strategy

DSCv3 is a standalone executable, not part of the OS. Three install
sources:

1. **`winget`** (default on Windows Server 2025 / Windows 11 24H2+):
   `winget install --id Microsoft.DSC --silent --accept-source-agreements`.
   Depends on RFC 0020 (winget module).
2. **GitHub release** (fallback for older Windows without winget):
   download the latest signed asset from
   `https://github.com/PowerShell/DSC/releases`, extract to
   `C:\Program Files\dsc\`, add to PATH.
3. **`path`**: operator has already installed `dsc.exe` and points the
   module at it explicitly. Useful for offline / air-gapped catlets.

If `install: skip` and `dsc.exe` isn't on PATH, the module fails fast
with a clear error rather than silently no-op'ing.

### Reboot handling

DSCv3 configurations can produce a "reboot required" result per resource.
Aggregate across the document; if any resource asked for reboot AND the
configuration succeeded otherwise, return `ModuleOutcome.Reboot` so the
StageRunner's reboot-and-continue plumbing (RFC 0007's exit-1003
convention) resumes after the reboot.

### IWindowsOs surface

`Task<DscApplyResult> RunDscConfigAsync(string documentPath, string verb, CancellationToken)`
— wraps `dsc.exe config <verb> --file <path>`. The decorator
(`DryRunWindowsOs`) intercepts to print what would be applied without
actually running `dsc.exe`.

`DscApplyResult` carries: `ExitCode`, `RebootRequested` (bool), and
the JSON output of `dsc.exe` (for diagnostics).

## Retirement of the `dbosoft/windsc` gene

Once this module lands, the `windsc` gene's two workarounds become
obsolete:

- The NuGet bootstrap was needed because DSCv1's `Install-Module` of
  DSC resources required the NuGet provider. DSCv3 doesn't use
  `Install-Module` at all for the runtime.
- The self-signed cert was needed because MOFs encrypted credentials
  against it. DSCv3 doesn't use MOF-cert encryption.

The gene can either:
- Be retired (operators move to the cloud-config `dsc:` key directly), or
- Stay as a transitional gene that just enables the `dsc:` key (no
  scripts, no certs, no NuGet — the gene becomes a near-empty
  passthrough).

We recommend retirement: bumping the `windsc` gene to v2.0 with a
deprecation banner + a one-line upgrade note pointing at `dsc:` in
cloud-config.

## Open questions

- **Backwards compat for DSCv1 resources** — DSCv3 ships a
  `PSDesiredStateConfiguration` adapter that runs v1 resources. Does it
  cover the resources eryph users actually rely on (DSC resource kit
  bits, community modules like xPSDesiredStateConfiguration)? Needs a
  smoke test against a real `windsc`-based catlet before we sunset the
  gene.
- **Server 2016 / 2019 support** — DSCv3 requires PowerShell 7.4+ to
  run resource adapters; both OSs ship Windows PowerShell 5.1 only.
  Operators on 2016/2019 need `pwsh` installed (which the
  cloud-config can do via `winget` or `chocolatey`, RFC 0020/0021).
  Module should detect missing pwsh and produce a clear error
  pointing at the install path.
- **Reboot-required signal granularity** — DSCv3 returns per-resource
  results. Should we expose `reboot_after_each: true` so the
  StageRunner reboots between resources that requested it, or always
  batch the reboot to the end? Tentative: always batch — matches the
  cloud-init mental model.
- **Configuration document size** — inline YAML in cloud-config can
  bloat user-data. For large configs the recommended pattern is
  `document_path` (write the YAML via `write_files` first) or
  `document_url` (fetch at apply time). Document this in the user
  docs and `egs-service validate` warns if an inline document is
  > 50 KiB.

## Cross-references

- [RFC 0007](0007-scripts-per-frequency-edge-cases.md) — reboot-and-continue
  convention.
- [RFC 0009](0009-module-list-split.md) — operators can disable via
  `disabledModules: [DscModule]` for catlets that prefer Chef / Puppet.
- [RFC 0020](0020-winget-module.md) / [RFC 0021](0021-chocolatey-module.md) —
  pwsh install pre-req on Server 2016/2019.
- [RFC 0022](0022-chef-module.md) — the cloud-init-native alternative
  for declarative convergence on Windows.

## Related work in eryph

- `dbosoft/windsc` gene at
  `S:\eryph\eryph-genes\genes\dbosoft\windsc\1.0\fodder\setup.yaml` —
  the current workaround this RFC retires.
- `dbosoft/winlab` gene — uses `windsc` indirectly; its catlets should
  switch to the native `dsc:` key once this module ships.
