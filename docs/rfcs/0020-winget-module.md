# RFC 0020 — `winget` cloud-config module

Status: Implemented

Implemented in commit `de11f16` (module) and `a6f754e` (Config-stage ordering).

## Problem

Eryph already ships a winget fodder gene (`gene:dbosoft/winget:install`)
that operators chain in to install packages at first boot. Bringing the
same surface into the provisioning agent gives a uniform cloud-config
schema, removes the duplicate logic in fodder, and lets the agent reason
about ordering and reboot-and-continue.

## What cloud-init does

Nothing. The cloud-init modules index
(<https://cloudinit.readthedocs.io/en/latest/reference/modules.html>)
has no winget plugin. Closest analogues are `cc_apt_configure` and
`cc_package_update_upgrade_install` — Linux-shaped, not portable.

## What cloudbase-init does

Nothing. No `winget.py` plugin exists in
<https://github.com/cloudbase/cloudbase-init/blob/master/cloudbaseinit/plugins/>.
Winget post-dates the cbi-active era; if a gene wants packages, it
writes a runcmd-script today.

## What Windows needs

`winget.exe` (the App Installer, an MSIX-packaged per-user tool) does
package install:

```
winget install --id <id> --exact --silent --accept-source-agreements --accept-package-agreements
               [--version <v>] [--source <source>] [--scope machine|user]
```

### The LocalSystem problem — and how the gene already solved it

The provisioning agent runs as `LocalSystem`. Two facts follow, both
learned the hard way by the existing gene and now treated as settled:

1. **winget must be *provisioned*, not installed.** `Add-AppxPackage`
   refuses to run under LocalSystem. The gene instead downloads the
   latest App Installer release (msixbundle + architecture-matched
   dependency `.appx` packages + license) from
   `microsoft/winget-cli` on GitHub and calls
   `Add-AppxProvisionedPackage -Online`. That registers winget for
   every user at their next logon and lands the package payload under
   `C:\Program Files\WindowsApps`.

2. **winget.exe does not run as LocalSystem out of the box.** Started
   outside a package activation context it cannot find its dependency
   DLLs (`0xC0000135`). The gene writes a wrapper,
   `C:\ProgramData\eryph\winget\winget-system.ps1`, that locates the
   newest App Installer + VCLibs + Windows App Runtime package
   directories, puts them on `PATH`, and execs the real `winget.exe`.
   Every subsequent invocation goes through the wrapper.

Under LocalSystem, only these combinations work and are therefore the
only ones the module supports:

- the **`winget` source** (the `msstore` source needs a logged-on user
  token; custom sources added via `winget source add` still work);
- **machine scope** (`--scope machine`) — per-user packages would land
  in the system profile.

### Platform support

- Windows 10 1809 (build 17763) or later, Windows 11.
- Windows Server 2022 (build 20348) or later. The current App Installer
  package cannot be provisioned on **Server 2019** — DISM rejects its
  manifest — so the module logs Error and fails there.

## Decision

Adopt the gene's proven **LocalSystem-wrapper** approach wholesale. The
earlier draft proposed running winget as a cloud-config user via a
scheduled task ("winget has no obvious LocalSystem mode"); that premise
is wrong — the gene runs winget as LocalSystem today. Dropping the
runner-user machinery removes the password-handling, reboot-survival,
and user-selection problems entirely, and keeps the module ordering-free
with respect to `UsersGroupsModule`.

### POCO shape

```csharp
public sealed record WingetConfig
{
    public bool? InstallWinget { get; init; }                     // provision App Installer if missing (default true)
    public IReadOnlyList<WingetSource>? Sources { get; init; }
    public IReadOnlyList<WingetPackage>? Packages { get; init; }
}

public sealed record WingetSource
{
    public string Name { get; init; } = "";
    public string Argument { get; init; } = "";
    public string? Type { get; init; }       // e.g. Microsoft.Rest
    // --accept-source-agreements is always passed; no per-source flag.
}

public sealed record WingetPackage
{
    public string Id { get; init; } = "";
    public string? Version { get; init; }     // pinned version, optional
    public string? Source { get; init; }      // defaults to "winget"; "msstore" unsupported under LocalSystem
    public string? Scope { get; init; }       // defaults to "machine"; "user" unsupported under LocalSystem
}
```

Top-level YAML:

```yaml
winget:
  install_winget: true
  packages:
    - id: Git.Git
      version: 2.46.0
    - id: 7zip.7zip
```

### Module

- `Eryph.GuestServices.Provisioning.Modules.WingetModule`
  (Stage = Config, Order before `RuncmdModule`, Frequency = PerInstance).
  Config stage so packages are installed before `runcmd` executes —
  cloud-init installs packages in the config stage and only executes
  runcmd in the final stage, and our `RuncmdModule` executes in Config,
  so the package modules run earlier in Config to preserve that. No
  ordering dependency on `UsersGroupsModule` — winget runs as
  LocalSystem, not as a user.
- Bootstrap: if `install_winget` is unset/`true` and winget is not
  provisioned, provision it (download + `Add-AppxProvisionedPackage`).
  If `install_winget: false` and winget is missing while `packages:` is
  non-empty → log Warning and exit Ok (skipped; operator opted out of
  bootstrap) and do **not** touch the wrapper.
- Wrapper: once winget is known present (whether we just provisioned it
  or it shipped with the image — Windows 11 and Server 2025 ship App
  Installer provisioned), **always** ensure the LocalSystem wrapper via
  `EnsureWingetWrapperAsync` before any `winget` invocation. This is a
  separate step from provisioning precisely because the provisioning
  step is skipped on images that already have winget, yet the wrapper
  (our artifact) still has to be written. A failed wrapper smoke-test
  (`winget --version`) fails the module.
- Unsupported platform (Server 2019 / build below the minimum) → log
  Error, exit failed.
- For each source: `winget source add ...` through the wrapper.
- For each package: `winget install --exact --source winget --scope
  machine --silent --accept-source-agreements
  --accept-package-agreements --disable-interactivity` through the
  wrapper. Pass `--version` when pinned.
- Exit-code handling (match the gene):
  - `0` → success;
  - `0x8A15002B` (-1978335189, no newer version available) → success;
  - `0x8A150061` (-1978335135, already installed) → success;
  - anything else → log Error, record the package as failed, continue
    to the next (continue-on-failure parity with `runcmd`,
    [RFC 0007](0007-scripts-per-frequency-edge-cases.md)).
- If any package failed, the module's outcome is failed.

### IWindowsOs additions

```csharp
Task<bool> IsWingetProvisionedAsync(CancellationToken ct);
Task ProvisionWingetAsync(CancellationToken ct);              // download + Add-AppxProvisionedPackage (NOT the wrapper)
Task EnsureWingetWrapperAsync(CancellationToken ct);          // (re)write winget-system.ps1 + smoke-test --version
Task<RunCommandResult> RunWingetAsync(IReadOnlyList<string> argv, CancellationToken ct);  // via the LocalSystem wrapper
```

`ProvisionWingetAsync` and `EnsureWingetWrapperAsync` are deliberately
split so the wrapper is written even on images that ship winget
provisioned (where provisioning is skipped). `RunWingetAsync` and
`ProvisionWingetAsync` carry generous safety-net timeouts so a hung
installer / download cannot block the Final stage forever. No
`RunCommandAsUserAsync` — the earlier draft needed it only for the
scheduled-task approach that this RFC drops.

## Cross-references

- [RFC 0007](0007-scripts-per-frequency-edge-cases.md) — continue-on-
  failure parity with `runcmd`.
- [RFC 0009](0009-module-list-split.md) — operators who keep using the
  winget fodder gene disable the module via `disabledModules:
  [WingetModule]`.
- [RFC 0021](0021-chocolatey-module.md) — sibling package-install
  module (LocalSystem, no AppX provisioning, no wrapper).

## Open questions

- **Idempotency of pinned versions.** `winget install <id> --version
  <v>` on a system already holding a different version is currently a
  no-op (returns `0x8A150061`, treated as success). We do **not** pass
  `--force`: it re-runs the installer on an already-satisfied package
  and bypasses winget's installer-integrity checks. Moving an existing
  install to a different pinned version is therefore not yet supported;
  revisit if operators need it.
- **No auto-update of an existing winget.** If winget is already
  provisioned we skip provisioning, so a stale App Installer on the
  image is not upgraded (a divergence from the gene, which always
  re-provisioned). The module is `PerInstance`, so re-provisioning would
  only cost one GitHub download per instance — but skipping avoids the
  network dependency entirely when winget is present. Acceptable for v1;
  an operator who needs a newer winget rebuilds the base image.
- **`install_winget` default.** The gene always provisions. The module
  defaults `install_winget` to `true` to match; an operator on a
  locked-down image sets it `false` to skip the GitHub download.
- A future `package_install` umbrella (winget on Windows, apt/yum on
  Linux, choco as a fallback) would unify the schema. Out of scope
  here; `WingetConfig` is designed to be the obvious Windows branch of
  it. See [RFC 0021](0021-chocolatey-module.md) open questions.
