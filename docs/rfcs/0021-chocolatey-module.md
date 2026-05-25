# RFC 0021 — `chocolatey` cloud-config module

Status: Draft

## Problem

Legacy and production Windows operators still lean heavily on
Chocolatey for package management. Winget is the modern choice
([RFC 0020](0020-winget-module.md)) but operators with existing
Chocolatey-based gene fleets want the same declarative schema rather
than maintaining two parallel mechanisms.

The pair (winget + chocolatey) covers the modern and the established
Windows package ecosystem. A future umbrella RFC may multiplex these
under a single `packages:` key; this RFC ships the chocolatey-specific
schema first.

## What cloud-init does

Nothing. No chocolatey module in the cloud-init reference
(<https://cloudinit.readthedocs.io/en/latest/reference/modules.html>).

## What cloudbase-init does

A third-party `chocolatey.py` plugin has existed in community forks
historically but never merged upstream. The reference plugin tree
(<https://github.com/cloudbase/cloudbase-init/blob/master/cloudbaseinit/plugins/>)
has no chocolatey entry. Cbi-shipping operators install via runcmd or
a fodder script.

## What Windows needs

Chocolatey is a PowerShell-based package manager that operates from
`C:\ProgramData\chocolatey\` and runs as LocalSystem cleanly — no user
impersonation needed (the key difference from winget). The relevant
commands:

```powershell
# Bootstrap (if missing):
Set-ExecutionPolicy Bypass -Scope Process -Force
iex ((New-Object Net.WebClient).DownloadString('https://community.chocolatey.org/install.ps1'))

# Source register:
choco source add -n <name> -s <url> --priority <n>

# Install:
choco install <name> -y --version <v> --params '<params>'
```

Exit codes: 0 = success, 1641 / 3010 = reboot required. Map the reboot
codes to `ModuleOutcome.RebootRequested` to ride the
[RFC 0007](0007-scripts-per-frequency-edge-cases.md) contract.

## Tentative direction

### POCO shape

```csharp
public sealed record ChocolateyConfig
{
    public bool? InstallChocolatey { get; init; }                  // bootstrap if missing
    public IReadOnlyList<ChocolateySource>? Sources { get; init; }
    public IReadOnlyList<ChocolateyPackage>? Packages { get; init; }
}

public sealed record ChocolateySource
{
    public string Name { get; init; } = "";
    public string Url { get; init; } = "";
    public int? Priority { get; init; }
}

public sealed record ChocolateyPackage
{
    public string Name { get; init; } = "";
    public string? Version { get; init; }
    public string? Params { get; init; }    // --params '<...>'
}
```

Top-level YAML:

```yaml
chocolatey:
  install_chocolatey: true
  sources:
    - name: chocolatey
      url: https://community.chocolatey.org/api/v2/
      priority: 1
  packages:
    - name: 7zip
      version: '24.07'
      params: '/InstallDir=C:\\tools\\7zip'
```

### Module

- `Eryph.GuestServices.Provisioning.Modules.ChocolateyModule`
  (Stage = Final, Order after `WingetModule` so an operator who lists
  both gets winget first. Frequency = PerInstance.)
- Bootstrap: if `install_chocolatey: true` and `choco.exe` is missing,
  run the bootstrap one-liner. Skip if already present.
- Sources: for each entry, `choco source add` (idempotent — choco's
  source-add is "update if exists").
- Packages: for each entry, `choco install <name> -y` plus
  `--version`, `--params` as supplied. Capture exit code:
  - 0 → success.
  - 1641 / 3010 → success, mark `RebootRequested`.
  - anything else → log Error, continue (parity with `runcmd`).
- If any package returned 1641/3010, the module's final outcome is
  `RebootRequested`. Reboot-and-continue picks up the remaining work.

### Runs as LocalSystem

Unlike `WingetModule`, chocolatey is happy in the service context.
We invoke `choco.exe` directly through `RunArgvCommandAsync` — no user
impersonation, no scheduled-task dance. This makes the module
considerably simpler than winget and arguably the safer default.

### IWindowsOs additions

```csharp
Task<bool> IsChocolateyInstalledAsync(CancellationToken ct);
Task BootstrapChocolateyAsync(CancellationToken ct);
```

Both are thin wrappers; everything else reuses the existing
`RunArgvCommandAsync` surface.

### Validation

- Packages without a `name` → validation error.
- `priority` outside `1..100` → validation error.
- `install_chocolatey: false` + missing `choco.exe` + non-empty
  `packages:` → log Warning and exit Ok (skipped); operator opted out
  of bootstrap on a fresh image.

## Cross-references

- [RFC 0007](0007-scripts-per-frequency-edge-cases.md) — exit codes
  1641 / 3010 ride the reboot-and-continue contract.
- [RFC 0009](0009-module-list-split.md) — operators on
  winget-only images disable via `disabledModules: [ChocolateyModule]`.
- [RFC 0020](0020-winget-module.md) — sibling package-install
  module; same POCO patterns, different runner context.

## Open questions

- Should a future umbrella RFC define `packages:` with provider
  selection (`provider: winget | chocolatey | auto`)? Likely yes —
  worth a stand-alone RFC once both this and 0020 are implemented and
  we see the real overlap.
- Bootstrap URL — community.chocolatey.org is the canonical endpoint
  but an operator behind a proxy needs an override. Add a
  `bootstrap_url:` field? Tentative: defer to a follow-up; for v1 use
  the canonical URL and document the limitation.
- `choco upgrade` semantics — when a pinned version is supplied and
  the system has a higher version, do we downgrade? Tentative: yes
  with `--allow-downgrade` only when the version field is explicitly
  set. Otherwise leave existing installs alone.
- Should the module check that the catlet has internet access before
  attempting bootstrap? Tentative no — failing fast with the network
  error is more useful than a silent skip.
