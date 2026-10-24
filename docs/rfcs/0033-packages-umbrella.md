# RFC 0033 — `packages` cloud-config umbrella

Status: Accepted

## Problem

[RFC 0020](0020-winget-module.md) and [RFC 0021](0021-chocolatey-module.md)
ship provider-specific blocks (`winget:`, `chocolatey:`). cloud-init
already defines a generic `packages:` key, and cross-cloud catlets carry
it. We want a single declarative `packages:` surface that works the
cloud-init way and, on Windows, routes to winget / chocolatey — without
duplicating the install machinery.

## What cloud-init does

`packages:` is a list whose entries are one of:

- a bare string — `- git`;
- a `[name, version]` pair — `- [git, 2.46.0]`;
- a manager-keyed mapping — `- {apt: [pkgA], snap: [pkgB]}` (cloud-init
  23.x+). The manager keys are the OS package managers (`apt`, `snap`).

cloud-init installs these in the **config** stage; `runcmd` is written in
config but executed in the final stage, so packages are available to
`runcmd`.

## What cloudbase-init does

Nothing — no generic package key on Windows.

## Decision

Make `packages:` an active, cross-platform key that mirrors cloud-init's
union exactly and extends the manager set with `winget` and
`chocolatey`:

```yaml
packages:
  - 7zip.7zip                 # bare -> default provider (winget)
  - [Git.Git, 2.46.0]         # [name, version] -> default provider
  - winget:
      - Microsoft.PowerShell
      - id: Foo.Bar
        version: 1.2.3
    chocolatey:
      - name: nodejs
        version: 20.11.0
  - apt:                      # Linux manager: accepted, no-op on Windows
      - htop
```

- **Default provider = winget.** A bare name / pair routes to winget.
- **Manager groups** route to their provider (`winget`, `chocolatey` /
  `choco`). Linux managers (`apt`, `snap`, `yum`, `dnf`, `zypper`, …) are
  accepted and no-op on Windows.
- **No new executor module.** A resolver (`PackageUmbrella`) turns the
  `packages:` entries into `WingetPackage` / `ChocolateyPackage` lists,
  and the existing `WingetModule` / `ChocolateyModule` install them —
  combined with their own `winget:` / `chocolatey:` block lists. So the
  umbrella inherits those modules' LocalSystem handling, reboot
  contract, exit-code mapping and e2e coverage for free.
- **Stage.** Because the modules run in the Config stage before
  `RuncmdModule` ([RFC 0020](0020-winget-module.md)), `packages:` are
  available to `runcmd`, matching cloud-init.

### Scope boundary

Advanced per-package options — winget `source` / `scope`, chocolatey
`params` — are **not** expressed in the umbrella. The umbrella covers the
common name(+version) case; power users set those through the dedicated
`winget:` / `chocolatey:` blocks, which run in the same pass.

### Model

```csharp
public sealed record PackageEntry
{
    public string? Name { get; init; }       // bare / [name, version]
    public string? Version { get; init; }
    public IReadOnlyDictionary<string, IReadOnlyList<ManagerPackage>>? Managers { get; init; }
}

public sealed record ManagerPackage
{
    public string Name { get; init; } = "";  // name or (winget) id
    public string? Version { get; init; }
}
```

The YAML union is handled by two `IYamlTypeConverter`s
(`PackageEntryYamlTypeConverter`, `ManagerPackageYamlTypeConverter`),
following the `RuncmdEntry` precedent, with read + write so the document
round-trips.

## Cross-references

- [RFC 0020](0020-winget-module.md) — winget executor + Config-stage
  ordering.
- [RFC 0021](0021-chocolatey-module.md) — chocolatey executor.

## Open questions

- **Provider auto-detection.** The default is fixed to winget. An `auto`
  mode (winget if provisionable, else chocolatey) was considered and
  deferred — a fixed default is more predictable.
- **Linux activation.** On Linux the winget/chocolatey managers are
  meaningless and the apt/snap managers are the real ones; a future
  Linux package module would consume the same `packages:` model.
