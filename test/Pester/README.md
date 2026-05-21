# End-to-end Pester suite for the guest provisioning agent

These tests drive the real `egs-provisioning.exe` binary against the cloud-config
fixtures in `samples/`. They are the complement to the C# unit tests in
`test/Eryph.GuestServices.Provisioning.Tests/`.

## Why this suite exists

Unit tests mock out `IWindowsOs`, `IDataSource`, and friends. That is fast
and precise, but it leaves a large gap: any wiring change in `Program.cs`,
any container registration mistake, any new top-level CLI argument, any
real-world YAML the converters don't yet handle — none of it is exercised
until somebody runs the binary by hand. This suite closes that gap by
pushing real samples through the real binary in `--dry-run` mode.

## Prerequisites

- Windows host (the agent is Windows-only; many tests touch Windows-specific paths).
- PowerShell 5.1+ or PowerShell 7+.
- Pester 5.5.0 or newer:

  ```powershell
  Install-Module Pester -MinimumVersion 5.5.0 -Force -Scope CurrentUser
  ```

- A built `egs-provisioning.exe`. From the repo root:

  ```powershell
  dotnet build src\Eryph.GuestServices.Provisioning
  ```

  The default lookup path is
  `src\Eryph.GuestServices.Provisioning\bin\Debug\net10.0\win-x64\egs-provisioning.exe`.
  To use a different build (e.g. a Release publish), set the
  `EGS_PROVISIONING_EXE` environment variable to the full path:

  ```powershell
  $env:EGS_PROVISIONING_EXE = 'C:\path\to\egs-provisioning.exe'
  ```

## Running

From the repo root:

```powershell
Invoke-Pester .\test\Pester\
```

Add `-Output Detailed` while iterating to see every assertion:

```powershell
Invoke-Pester .\test\Pester\ -Output Detailed
```

To run a single `Describe` block:

```powershell
Invoke-Pester .\test\Pester\Invoke-Samples.Tests.ps1 -FullNameFilter '*Sample 03*'
```

## What the tests cover

- `validate` over every well-formed sample (exits 0).
- `run --dry-run` over every well-formed sample, with output assertions for
  the user names, paths, hostnames, and runcmd strings the agent should mention.
- `status` after a dry-run (state directory should remain empty per the
  dry-run contract).
- `validate` against intentionally-broken samples in
  `samples/cloud-configs/broken/` (exits non-zero with descriptive stderr).
- `reset` removes a seeded `state.json` and is a no-op when none exists.
- `collect-logs` produces a non-empty zip at the requested output path.

The tests deliberately do **not** try to reproduce every internal handler
call from PowerShell — that's the C# unit tests' job. The contract verified
here is "does the binary do the right thing when you push real YAML through
it".

## CLI contract assumed

The harness assumes the following CLI shape, which is delivered by the
parallel CLI agent on the `provisioning-agent` branch:

| Command | Arguments |
|---|---|
| `validate` | `--user-data <path>` |
| `run` | `--user-data <path> --dry-run [--state-dir <dir>]` |
| `status` | `[--state-dir <dir>]` |
| `reset` | `[--state-dir <dir>]` |
| `collect-logs` | `--output <zipPath>` |

If the CLI evolves, update the argument arrays at the top of each `It`
block; the asserts on stdout/stderr should keep working.

## Known limitations

- **Windows-only.** A handful of paths are `C:\...`-style; tests that touch
  filesystem ACLs and `New-LocalUser` plumbing will not run elsewhere.
- **Dry-run only.** Tests never mutate the host: no users, no files
  outside the OS temp directory, no registry changes.
- **Best-effort log assertions.** Each `Assert-EgsOutputContains` looks for
  a substring (e.g. a user name) that the agent is expected to log. If a
  log message is rephrased, update the assertion to match the new text.
- **No CI gating yet.** The suite ships green-field; it is not part of the
  Azure Pipelines build. Run it locally before publishing a binary release.

## Layout

```
test/Pester/
  Invoke-Samples.Tests.ps1                   # The Describe blocks.
  README.md                                  # You are here.
  helpers/
    Invoke-EgsProvisioning.ps1               # Locates + invokes the exe.
    Test-Provisioning.ps1                    # Custom assertions + temp dirs.
```
