# Azure VM Extension — sketch

A first-draft scaffold for shipping `egs-service` as an Azure VM Extension. **Not production-ready** — see "What's missing" below.

## What this is

An Azure VM Extension that installs `egs-service` and runs the provisioning agent once. On Azure, the VM Agent (WinGA) writes `CustomData` to `C:\AzureData\CustomData.bin` but does not consume it — this extension is the consumer. It is the cloudbase-init alternative for Windows Azure VMs.

The Hyper-V-socket SSH side of EGS is **not** in scope here: there is no Hyper-V host on Azure to talk to.

## Layout

```
packaging/azure-extension/
├── HandlerManifest.json   declares lifecycle entry points + OS support
├── install.cmd            invoked once at first deploy
├── enable.cmd             invoked at install, restart, and on settings change
├── disable.cmd            invoked before update / when extension is disabled
├── uninstall.cmd          invoked when the extension is removed
├── update.cmd             invoked when a new extension version replaces this one
└── bin/
    ├── Handler.ps1        thin entry point invoked by the .cmd wrappers
    └── HandlerLib.psm1    lifecycle logic; importable for unit tests
```

At build time the EGS binaries are dropped into `payload/` alongside the handler. `install.cmd` copies them to `C:\Program Files\eryph\guest-services\` and registers the Windows service — same path the ISO installer uses, so the two cannot collide.

The `ServiceName` and `InstallRoot` are overridable via `EGS_HANDLER_SERVICE_NAME` / `EGS_HANDLER_INSTALL_ROOT` environment variables. Defaults match the production layout; the test harness sets both to test-only values to avoid mutating the host.

## Lifecycle

| Hook        | What happens                                                                 |
|-------------|------------------------------------------------------------------------------|
| `install`   | Copy `payload/` → `C:\Program Files\eryph\guest-services\`; register service. |
| `enable`    | Start the service; poll `egs-service status` until terminal; write `.status`. |
| `disable`   | Stop the service. Files stay on disk.                                        |
| `uninstall` | Stop + remove service; remove install directory.                             |
| `update`    | No-op — `disable` (old) + `install` (new) + `enable` (new) handles the move. |

Status JSON is written to the path the Azure VM Agent passes in `HandlerEnvironment.json` (`status/<sequenceNumber>.status`). Enable polls `egs-service status --json` and converts the EGS terminal state (`completed` / `failed`) into the Azure extension status (`success` / `error`).

## Settings schema

`publicSettings` (none required today; reserved for future use):

```json
{
  "stage": "final",        // optional: override pipeline stage
  "skipCustomData": false  // optional: install service but don't run provisioning
}
```

`protectedSettings`: unused in v1. CustomData is already encrypted by the platform and decrypted by WinGA before it lands on disk; we do not need an additional secret channel.

## Local testing

Two layers, both run on the host without an Azure VM:

```powershell
# 1. Unit + simulator tests for HandlerLib (Pester 5, all side effects mocked)
.\test\azure-extension\Run-Tests.ps1

# 2. Manual smoke: stage a fake Azure-VM-Agent environment and drive the
#    lifecycle. Set EGS_HANDLER_SERVICE_NAME + EGS_HANDLER_INSTALL_ROOT to
#    test-only values; the simulator does this by default.
.\test\azure-extension\Invoke-FakeAzureVmAgent.ps1 -BaseDir C:\Temp\egs-ext-smoke `
  -PublicSettings @{ skipCustomData = $true } -NoExecuteHandlers
```

The full lifecycle (real sc.exe + service start/stop) is opt-in and intended for an isolated VM, not the dev host.

## Building the extension package

```powershell
.\packaging\pack-azure-extension.ps1
```

Produces `packaging\azure-extension\dist\eryph-guest-services-<version>.zip` with the layout the Azure VM Agent expects (manifest + .cmd wrappers + bin/ + payload/bin/egs-service.exe).

## What's missing (before this could ship)

- **Publisher onboarding.** Partner Center "Azure VM Extension" offers are gate-kept by the Azure extensions PG (`azurevmextensions@microsoft.com`). The handler is the easy half; getting the publisher namespace registered is the hard half.
- **Code signing.** The handler zip and embedded binaries must be Authenticode-signed; self-signed packages are rejected at submission.
- **Validation matrix.** Microsoft runs the handler through its test harness against each declared OS SKU (`Windows Server 2016/2019/2022/2025`, etc.). Each needs an integration pass.
- **Telemetry / heartbeat.** `reportHeartbeat: false` is fine for one-shot provisioning, but if we ever want long-running status we need to wire up `heartbeat/heartbeat.json`.
- **Linux handler.** Out of scope for v1. If we ever publish one, it is a separate package with `.sh` entry points and a different OS matrix.
- **In-VM e2e.** Layer 1 unit tests + Layer 2 simulator give host-side coverage. A VM-based test that actually invokes `sc.exe create` against a real SCM is the natural next step; it would run inside a base catlet with a test-scoped service name.

## References

- [Azure VM Extension handler protocol](https://learn.microsoft.com/azure/virtual-machines/extensions/features-windows)
- [HandlerManifest.json schema](https://github.com/Azure/azure-marketplace/wiki/Extension-Build-and-Publish-Guide)
- [Partner Center — VM Extension offers](https://learn.microsoft.com/partner-center/marketplace-offers/azure-vm-extension)
