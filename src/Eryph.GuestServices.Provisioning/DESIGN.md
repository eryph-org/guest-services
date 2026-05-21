# Eryph Guest Provisioning Agent — Design

Status: **draft, in active implementation**. Shared blueprint for the coding agents working on this branch.

## Goal

Replace cloudbase-init on Windows guests with an eryph-owned provisioning agent that:

1. Consumes standard cloud-init `#cloud-config` userdata (compatible subset, see Scope).
2. Reads metadata + userdata from a NoCloud / ConfigDrive / OVF data source on first boot.
3. Applies configuration to the Windows guest via deliberate, idempotent handlers.
4. Supports reboot-and-continue mid-sequence (the cloudbase-init `exit 1003` pattern, modeled cleanly).
5. Reports provisioning state back to the host via the existing Hyper-V KVP plumbing already in `Eryph.GuestServices.HvDataExchange.Guest`.

Linux guests continue to use real cloud-init. **This agent is Windows-only.**

## Scope of the v1 implementation

Six directives, chosen from the eryph-genes audit:

| Directive | Cloud-config keys | Notes |
|---|---|---|
| `set_hostname` | `hostname`, `fqdn`, `preserve_hostname` | Sets computer name; may require reboot |
| `users` | `users`, `groups` | Local users + groups; password setup via separate path |
| `set_passwords` | `chpasswd`, `password`, `ssh_pwauth` | Local password assignment |
| `ssh_authorized_keys` | `ssh_authorized_keys` | Writes keys to user's `~/.ssh/authorized_keys` (Windows OpenSSH layout) |
| `write_files` | `write_files` | Path, content (with optional `base64` / `gzip+base64` encodings), permissions, owner |
| `runcmd` | `runcmd` | Sequence of commands; each entry is `string` or `list<string>` |

Out of scope for v1: timezone, locale, NTP, ca_certs, network-config v2 (network config is provided by eryph-zero through the ConfigDrive; the agent reads it but applying it on Windows is deferred to v2).

## Architecture

Three packages, mirroring `Eryph.ConfigModel.Catlets` / `.Catlets.Yaml` shape:

```
src/Eryph.GuestServices.CloudConfig/              — POCOs + validators (no YAML, no Windows code)
src/Eryph.GuestServices.CloudConfig.Yaml/         — YamlDotNet converters, lenient parsing
src/Eryph.GuestServices.Provisioning/             — agent host: DI, datasources, stage runner, handlers
test/Eryph.GuestServices.CloudConfig.Tests/
test/Eryph.GuestServices.CloudConfig.Yaml.Tests/
test/Eryph.GuestServices.Provisioning.Tests/
```

### Process model

`egs-provisioning.exe` runs as a Windows Service (`Microsoft.Extensions.Hosting.WindowsServices`), distinct from `egs-service`. It executes the configured stages, persists state, exits when done. On reboot-requested it persists "resume from stage X" state and the OS-level service start triggers the next run.

### Stage model

Stages run in order. Each stage runs all its handlers; a handler can declare `RebootRequested` to stop after the stage and resume next boot.

```
Discovery   → bring up the datasource, parse metadata + userdata
Hostname    → set_hostname (may reboot)
Users       → users, set_passwords, ssh_authorized_keys
Files       → write_files
Commands    → runcmd
Finalize    → mark complete, report to host via KVP
```

State is JSON in `%ProgramData%\eryph\provisioning\state.json`. Schema:

```json
{
  "instanceId": "abc-123",
  "completedStages": ["Discovery", "Hostname"],
  "completedHandlers": ["UsersHandler", "WriteFilesHandler"],
  "rebootCount": 1,
  "startedAt": "...",
  "lastUpdated": "..."
}
```

A new `instanceId` (from metadata) resets state. Within an instance, completed handlers are idempotent skips on re-run.

### DI (SimpleInjector)

Composition root in `Program.cs` builds a `Container`, registers:

- `IDataSource` implementations (collection)
- `IStateStore` (default: file-based)
- `IStageRunner`
- `IHandler` implementations (collection, ordered via stage attribute)
- `IWindowsOs` and its CIM/P-Invoke implementation
- `ICloudConfigSerializer` (the YAML entry point)
- `IHostStatusReporter` (writes to KVP)

SimpleInjector's `AddSimpleInjector` integration is used so the worker host can resolve.

## Package responsibilities

### `Eryph.GuestServices.CloudConfig`

**Pure model** + **validators**. No YAML, no Windows, no I/O.

- POCOs for the 6 directives. Naming follows cloud-init keys (snake_case in YAML, PascalCase in C#). Properties are nullable (missing = null), as in `CatletConfig`.
- Root `CloudConfig` POCO carries all directive sub-objects.
- Validators using `LanguageExt.Validation<Error, T>` from `Dbosoft.Functional`, following `Eryph.ConfigModel.Core.Validation` patterns. Each validator lives in a `Validations/` folder and returns `Validation<Error, CloudConfig>` so errors aggregate.
- Reuse `Eryph.ConfigModel.Core.Validation.Validations` and `EryphName` patterns. Where cloud-config has its own naming rules (e.g. Windows usernames have different constraints), introduce dedicated newtypes (`WindowsUserName`, `UnixFilePermissions`, etc.) following `EryphName<T>` shape.
- No deserialization concerns here — accept whatever the YAML layer produces and validate semantics.

### `Eryph.GuestServices.CloudConfig.Yaml`

**Lenient YAML deserialization** on top of `Eryph.ConfigModel.Yaml`.

- `CloudConfigYamlSerializer` static class — mirrors `CatletConfigYamlSerializer`:
  - `Deserialize(string yaml) → CloudConfig` 
  - Single `Lazy<IDeserializer>` with `UnderscoredNamingConvention`, `WithCaseInsensitivePropertyMatching`, type converters.
- Handles the `#cloud-config` leading comment line — strip it before parsing if present.
- **Custom converters** for cloud-config idioms (mirror `Catlets.Yaml/Converters/` layout):
  - Scalar→list promotion (e.g. `runcmd: echo hi` → one-element list).
  - User entry as `string` (just a name) OR object (full user record).
  - SSH authorized keys as `string` (single key) OR list.
  - `write_files[].permissions` as quoted string OR unquoted number (treated as octal string).
  - `write_files[].content` as plain string OR base64 OR gzip+base64 (decoded based on the sibling `encoding` field — leave content as-is at this layer; the handler decodes).
- Errors wrap via `InvalidConfigExceptionFactory.Create` for line/column-aware messages.
- Use `StringParser` from `Eryph.ConfigModel.Yaml` if we need to capture verbatim blocks (e.g. unparsed userdata fragments).

### `Eryph.GuestServices.Provisioning`

**The agent**.

- `Program.cs` — composition root, builds host + SimpleInjector container, wires logging.
- `DataSources/` — `IDataSource`, `NoCloudDataSource` (reads `meta-data` + `user-data` from a mounted volume labelled `cidata`/`config-2` or from a configured path), `ConfigDriveDataSource` (similar with OpenStack layout), `KvpDataSource` stub (reads from the existing KVP guest plumbing).
- `Stages/` — `IStage`, `StageRunner`, `StageAttribute` (mark a handler with its stage).
- `Handlers/` — `IHandler`, dispatcher. Each handler returns `HandlerOutcome` (Completed / RebootRequested / Failed).
- `State/` — `IStateStore`, `FileStateStore`.
- `Hosting/` — `ProvisioningWorker : BackgroundService` that runs `StageRunner` once and exits with the appropriate code.
- `Windows/` — `IWindowsOs` abstraction + concrete `WindowsOs` (CIM + P/Invoke). Used by handlers; **never** touched directly by stage runner code, so handlers stay testable with mocks.
- Handler implementations: `SetHostnameHandler`, `UsersGroupsHandler`, `SetPasswordsHandler`, `SshAuthorizedKeysHandler`, `WriteFilesHandler`, `RuncmdHandler`. Each is idempotent: re-running on an already-applied state is a no-op.

## Reboot-and-continue contract

A handler returns `HandlerOutcome.RebootRequested` instead of `Completed` when it needs a reboot to take effect. The stage runner:

1. Records the handler as completed (its work is done — only the reboot is missing).
2. Persists state.
3. Reports `provisioning_reboot_pending` via KVP.
4. Triggers `shutdown /r /t 0` (or returns a sentinel exit code if a higher-level orchestrator handles the reboot).

On next boot, the service starts, `StageRunner` reads state, sees the handler is already done, skips to the next handler/stage. Crucially: **handlers must not return RebootRequested if they did nothing**, because that would loop.

## Test discipline

- **Unit tests** for every validator and every YAML converter — fixture-based, fast.
- **Smoke test** in `Eryph.GuestServices.CloudConfig.Yaml.Tests`: theory-data enumerates all `*.yaml` files under `S:\eryph\eryph-genes\src` where they contain a `type: cloud-config` fodder block, extracts the `content`, runs it through `CloudConfigYamlSerializer.Deserialize`. Path is set via environment variable or skipped if not present (so CI without the gene tree still works).
- **Handler tests** in `Eryph.GuestServices.Provisioning.Tests`: substitute `IWindowsOs` with NSubstitute, assert handler calls the right OS methods with the right arguments. No real Windows mutation.
- **Integration test**: end-to-end pipeline with an in-memory data source feeding a real cloud-config YAML through the stage runner with a mocked OS layer.

Tests use **xunit 2.9.3 + AwesomeAssertions + NSubstitute**. Do **not** introduce Moq, FluentAssertions, or other test libraries.

## Conventions

- C# 13 (default with .NET 10), `Nullable enable`, `ImplicitUsings enable`.
- File-scoped namespaces.
- Primary constructors for DI-injected services.
- No fluent registration helpers — register directly in `Program.cs` so the graph is explicit.
- No `Microsoft.Extensions.DependencyInjection`-style registration extensions on `IServiceCollection` for our types — registrations go through SimpleInjector `Container`.
- Logging via `Microsoft.Extensions.Logging.ILogger<T>` everywhere — SimpleInjector wires it through the host integration.
- Use cloud-init source and cloudbase-init source as **inspiration only**. Do not port code or comments. Our model is cleaner.

## What's already done

- Branch `provisioning-agent` created.
- Six csproj skeletons added to `Eryph.GuestServices.sln` with package references resolved.
- Build is green.

## What's NOT yet done

Everything else — the model, the YAML, the agent, the handlers, the tests. The four coding agents pick this up from the placeholder source files.
