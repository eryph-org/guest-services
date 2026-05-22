# RFC 0020 — `winget` cloud-config module

Status: Draft

## Problem

Eryph already ships a winget fodder gene that operators chain in to
install packages at first boot. Bringing the same surface into the
provisioning agent gives a uniform cloud-config schema, removes the
duplicate logic in fodder, and lets the agent reason about ordering
(packages need users, users come from `UsersGroupsModule`).

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

`winget.exe` (the App Installer COM/exe) does package install:

```
winget install --id <id> --silent --accept-source-agreements --accept-package-agreements
               [--version <v>] [--source <source>] [--scope machine|user]
```

Source registration:

```
winget source add --name <name> --arg <url> --type <type> --accept-source-agreements
```

The tricky bit: **`winget` doesn't have an obvious LocalSystem mode.**
It's a per-user MSIX-packaged tool and reaches into the calling user's
profile to find package state. A service-context invocation
(`LocalSystem` SID) has historically been finicky — it can succeed for
machine-scoped MSI/EXE installs but often errors out on store packages
because the AppX activation requires a logged-on user token.

This is the same problem the existing gene already wrestles with
(scheduled task as the first cloud-config user, etc.).

## Tentative direction

### POCO shape

```csharp
public sealed record WingetConfig
{
    public IReadOnlyList<WingetSource>? Sources { get; init; }
    public IReadOnlyList<WingetPackage>? Packages { get; init; }
}

public sealed record WingetSource
{
    public string Name { get; init; } = "";
    public string Argument { get; init; } = "";
    public string? Type { get; init; }       // e.g. Microsoft.PreIndexed.Package
    public bool? Accept { get; init; }       // --accept-source-agreements
}

public sealed record WingetPackage
{
    public string Id { get; init; } = "";
    public string? Version { get; init; }    // pinned version, optional
    public string? Source { get; init; }     // winget | msstore | <custom>
    public string? Scope { get; init; }      // machine | user
}
```

Top-level YAML:

```yaml
winget:
  sources:
    - name: msstore
      argument: https://storeedgefd.dsx.mp.microsoft.com/v9.0
      type: Microsoft.PreIndexed.Package
      accept: true
  packages:
    - id: Microsoft.PowerShell
      version: 7.4.0
      source: winget
      scope: machine
```

### Module

- `Eryph.GuestServices.Provisioning.Modules.WingetModule`
  (Stage = Final — has to come after `UsersGroupsModule` so the
  cloud-config-created user exists. Frequency = PerInstance.)
- Pick the "winget runner user": first non-system user created by
  `UsersGroupsModule` this run. If none, log Warning and exit Ok
  (skipped — operator's cloud-config didn't include a user, so we have
  no eligible runner).
- For each source: `winget source add ...` invoked as the runner user.
- For each package: `winget install ...` invoked as the runner user.
- Exit code handling: 0 = success, 0x8A150061 = already installed
  (treat as success), anything else → log Error, continue to next
  package. (Match cloud-init's `cc_runcmd` "continue on failure" style.)

### Running as the runner user

Mechanism options: scheduled-task with `--RunLevel Highest --user
<name>` (the path the existing winget gene uses; robust on Server),
`LogonUser` + `CreateProcessAsUser` (needs the user's password — fine
during the PerInstance run, but stashing it across reboots is no-go),
PsExec (third-party, not shipping).

Tentative: scheduled task. We already use scheduled tasks for the
agent's own bootstrap, so the dependency is paid for.

### IWindowsOs additions

```csharp
Task<RunCommandResult> RunCommandAsUserAsync(
    string userName,
    IReadOnlyList<string> argv,
    CancellationToken ct);
Task<bool> IsWingetAvailableAsync(CancellationToken ct);
```

The user impersonation lives on `IWindowsOs` because future modules
(chocolatey is server-context, but a hypothetical `scoop` would be
user-context too) need the same primitive.

## Cross-references

- [RFC 0007](0007-scripts-per-frequency-edge-cases.md) — continue-on-
  failure parity with `runcmd`.
- [RFC 0009](0009-module-list-split.md) — operators who keep using the
  winget fodder gene disable via `disabledModules: [WingetModule]`.
- [RFC 0021](0021-chocolatey-module.md) — sibling package-install
  module that runs as LocalSystem (different mechanism, same schema
  shape).

## Open questions

- "Runner user" selection is fragile. If multiple cloud-config users
  exist, which one? First in `users:` list? First with `sudo`? First
  alphabetically? Tentative: first with `sudo` (admin), fall back to
  the first user, fall back to Warning + skip.
- Should we install winget itself if missing (App Installer MSIX)?
  Tentative no — Windows 10 22H2+ and Server 2022+ ship it; older
  images need a separate fodder bootstrap.
- A future RFC for a `package_install` umbrella (winget on Windows,
  apt/yum on Linux, choco as a fallback) would unify schema. Not in
  scope here, but design `WingetConfig` so it's the obvious
  Windows-shaped branch of that umbrella.
- Idempotency: `winget install <pinned-version>` on a system that
  already has the package at a different version — upgrade, downgrade,
  or no-op? Tentative: pass `--force` only when version is pinned.
