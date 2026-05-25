# Eryph Guest Services

[![License](https://img.shields.io/github/license/eryph-org/guest-services.svg)](LICENSE)

Eryph Guest Services (EGS) is a small service that runs **inside** a Windows or Linux VM. It bundles two independent capabilities in one binary:

1. **Userless SSH login over the Hyper-V socket** — a dedicated SSH server bound to the Hyper-V socket. Connect from the Hyper-V host with no IP, no firewall holes, no shared password. **Hyper-V only** — uses the Hyper-V socket directly, not the network.
2. **A cloud-init-compatible provisioning agent** — reads `#cloud-config` user-data from NoCloud / ConfigDrive / Azure / Hyper-V KVP on first boot and applies it (hostname, users, SSH keys, files, scripts, network). **Works on any Windows VM**; the datasources determine where the data comes from, the hypervisor doesn't matter.

The two capabilities share a binary but can be used independently. EGS is the runtime baked into every [eryph](https://www.eryph.io) catlet, where both run together; either capability can also be used on its own.

---

## Three use cases

EGS is shaped around three deployment shapes — pick the one that matches yours.

### (a) Hyper-V standalone — userless SSH, optional provisioning

You have a Hyper-V VM (no eryph) and want to talk to it without setting up networking. Install the ISO, run the host tool, and you have SSH over the Hyper-V socket. The provisioning agent ships in the same binary but you don't have to use it — your VM is already configured by whatever you used to build it.

→ [Quick start (standalone)](#quick-start-a-hyper-v-standalone)

### (b) Eryph — provisioning is the standard path

eryph injects a ConfigDrive ISO into every catlet at create time. The provisioning agent picks that up on first boot, applies your cloud-config fodder, and reports state back to the host via Hyper-V KVP. Userless SSH is enabled the same way as standalone. This is the path the project is built for.

→ [Quick start (eryph)](#quick-start-b-eryph)

### (c) Windows cloud-init alternative (WIP)

EGS as a general-purpose replacement for cloudbase-init on Windows VMs in scenarios outside eryph (OpenStack, custom KVM, hand-rolled NoCloud images). **This is work in progress and not fully supported yet.** The core RFCs (network-config, frequencies, semaphores, scripts, datasource lifecycle) are landed; the longer tail (jinja2, part-handler, boothook, OpenStack-specific quirks) is not. See [Windows cloud-init status](docs/user/explanation/windows-cloud-init-status.md) before relying on this in production.

---

## Components

| Binary | Where it runs | What it does |
|---|---|---|
| `egs-service` | Inside the VM (Windows service / systemd unit) | SSH server over Hyper-V socket + provisioning agent |
| `egs-tool` | On the Hyper-V host | SSH config writer, file transfer, status, key exchange |

The provisioning side of `egs-service` is a library; the CLI subcommands (`run`, `status`, `reset`, `validate`, `collect-logs`, `version`) are surfaced on the same `egs-service.exe` binary.

---

## Quick start (a) — Hyper-V standalone

### Install the host tool

```powershell
iex ((New-Object System.Net.WebClient).DownloadString('https://raw.githubusercontent.com/eryph-org/guest-services/main/src/Eryph.GuestServices.Tool/install.ps1'))
egs-tool initialize
```

### Install the guest

Download the installer ISO from [releases.dbosoft.eu/eryph/guest-services/](https://releases.dbosoft.eu/eryph/guest-services/), mount it in the VM, then:

```powershell
# Windows guest, as Administrator
D:\install.ps1
```

```bash
# Linux guest, as root
sudo /media/cdrom/install.sh
```

### Connect

```powershell
egs-tool add-ssh-config <VM-ID>
ssh <VM-ID>.hyper-v.alt
```

That's it — no IP, no firewall, no password. The provisioning agent is present but only runs if you feed it a datasource (NoCloud ConfigDrive ISO, Hyper-V KVP user-data, etc.); see [User docs](docs/user/index.md).

## Quick start (b) — eryph

### Build a catlet with cloud-config fodder

```yaml
name: my-vm
parent: dbosoft/winsrv2022-standard/starter
fodder:
  - name: bootstrap
    type: cloud-config
    content: |
      #cloud-config
      hostname: my-vm
      users:
        - name: alice
          groups: [Administrators]
          passwd: 'S0meStr0ngPass!'
      ssh_authorized_keys:
        - 'ssh-ed25519 AAAA...'
      write_files:
        - path: /ProgramData/marker.txt
          content: hello from cloud-config
      runcmd:
        - 'powershell -NoProfile -Command "Write-Host provisioned"'
```

```powershell
New-Catlet -Config (Get-Content .\my-vm.yaml -Raw) -Name my-vm
Start-Catlet -Name my-vm
```

The agent runs on first boot, applies the cloud-config, and reports `eryph.provisioning.state = completed` via KVP. Connect:

```powershell
egs-tool update-ssh-config
ssh my-vm.<project>.eryph.alt
```

→ See [Tutorial: first catlet with cloud-config](docs/user/tutorial/first-catlet-with-cloud-config.md) for a full walkthrough.

## Quick start (c) — Windows cloud-init alternative (WIP)

See [Windows cloud-init status](docs/user/explanation/windows-cloud-init-status.md) for what works today, what doesn't, and how to file feedback.

---

## What the provisioning agent supports

Cloud-init-compatible. If you've used `#cloud-config` before, the same payloads work here. Concrete coverage:

- **Modules**: `set_hostname`, `users`, `groups`, `set_passwords`, `ssh_authorized_keys`, `write_files`, `runcmd`, `scripts_user`, `apply_network_config`. Each declares a frequency (per-instance / per-boot / per-once).
- **User-data formats**: `#cloud-config`, `#!`, `#ps1*`, `#cloud-boothook`, `#include`, `#include-once`, multipart MIME (with base64 / no-close-delimiter tolerance).
- **Datasources**: NoCloud, ConfigDrive (OpenStack), Azure (with PA / WinGA coexistence), Hyper-V KVP.
- **Stages**: Local → Network → Config → Final. Reboot-and-continue (exit 1003) honored.
- **State**: Per-instance / per-boot / per-once semaphores under `%ProgramData%\eryph\provisioning\`.
- **Reporting**: Hyper-V KVP (`eryph.provisioning.state`, `eryph.provisioning.error`).

What's **not** supported (yet): jinja2 templating, part-handler, boothook execution as cloud-init does, OpenStack network_data.json (we read v1/v2 cloud-init network-config instead). See [differences from cloud-init](docs/user/explanation/differences-from-cloud-init.md).

→ Full [user documentation](docs/user/index.md) | [RFC index](docs/rfcs/README.md)

---

## SSH access

The guest exposes a dedicated SSH server on the Hyper-V socket (service ID `0000138a-facb-11e6-bd58-64006a7986d3` / Linux vsock port 5002). The host tool writes an SSH config that uses `hvc nc -t vsock <vmid> 5002` as the proxy command. Key exchange happens through Hyper-V KVP so no manual `ssh-copy-id` is needed.

```powershell
# Status of the guest agent in a VM
egs-tool get-status <VM-ID>

# Add a single VM to the SSH config
egs-tool add-ssh-config <VM-ID> [alias]

# Refresh the SSH config for all VMs / catlets
egs-tool update-ssh-config

# Connect
ssh <VM-ID>.hyper-v.alt          # standalone
ssh <catlet>.<project>.eryph.alt # eryph
```

### Configure the shell

By default interactive sessions land in `powershell.exe` (Windows) or `$SHELL` / `/bin/bash` (Linux). Override per-VM:

```powershell
egs-tool set-shell <VM-ID> --command pwsh.exe
egs-tool set-shell <VM-ID> --command pwsh.exe --arguments="-NoLogo -NoProfile"
egs-tool set-shell <VM-ID> --reset
```

SSH clients can also send `SHELL` / `SHELL_ARGS` env vars; the per-VM override always wins.

### File transfer

```powershell
egs-tool upload-file      <VM-ID> <local> <remote>  [--overwrite]
egs-tool upload-directory <VM-ID> <local> <remote>  [--recursive] [--overwrite]
egs-tool download-file    <VM-ID> <remote> <local>  [--overwrite]
egs-tool download-directory <VM-ID> <remote> <local> [--recursive] [--overwrite]
```

No `scp` — the channel is built on the same SSH-over-Hyper-V-socket transport. For interactive sync, use `unison` over the SSH tunnel.

---

## Why this exists

- Windows Server 2025 ships OpenSSH but [still no Hyper-V socket support](https://github.com/PowerShell/Win32-OpenSSH/issues/2200).
- [PowerShell Direct](https://learn.microsoft.com/en-us/powershell/scripting/learn/remoting/ps-direct) is Windows-only and has poor file-transfer performance.
- Linux vsock SSH is distro-dependent.
- For provisioning: cloudbase-init has known bugs (requires `filename=` in multipart MIME, ignores shebangs, several Python 2 / Windows-encoding traps). EGS is .NET-native, byte-clean, and ships fixes for these. See [differences from cloudbase-init](docs/user/explanation/differences-from-cloudbase-init.md).

---

## Requirements

**Host**: Windows 10/11 Pro/Enterprise/Education or Windows Server 2016+, Hyper-V enabled, administrator privileges.

**Guest**: Windows Server 2016+ / Windows 10+ or a modern Linux distribution with systemd. Hyper-V integration services enabled.

---

## Authentication

- Username: `egs` (don't change it)
- SSH public key authentication only — passwords disabled
- Keys exchanged via Hyper-V data-exchange service
- Host keys trusted automatically (transport is isolated to the Hyper-V socket)

```powershell
egs-tool get-ssh-key   # show the host-side public key
egs-tool initialize    # regenerate keys if needed
```

---

## CLI quick reference

### `egs-tool` (host)

| Command | What |
|---|---|
| `initialize` | Register Hyper-V service + generate SSH keys |
| `unregister` | Remove the Hyper-V integration service |
| `get-status <VM-ID>` | Check whether the guest agent is reachable |
| `get-ssh-key` | Print the host's SSH public key |
| `add-ssh-config <VM-ID> [alias]` | Add one VM to the SSH config |
| `update-ssh-config` | Refresh SSH config for every VM / catlet |
| `upload-file` / `upload-directory` | Push file(s) to a VM |
| `download-file` / `download-directory` | Pull file(s) from a VM |
| `set-shell <VM-ID>` | Override the interactive shell |
| `get-data --json <VM-ID>` | Dump Hyper-V KVP for the VM |

Flags: `--overwrite`, `--recursive` (directory commands), `--reset` (`set-shell`).

### `egs-service` (guest)

| Command | What |
|---|---|
| `(no args)` | Run as a Windows service / systemd unit |
| `run [--dry-run] [--user-data <path>] [--instance-id <id>] [--stage <s>] [--state-dir <dir>]` | Run the provisioning agent once |
| `status [--json] [--state-dir <dir>]` | Print current provisioning state |
| `reset [--logs] [--scripts] [--reset-once] [--keep-per-boot] [--state-dir <dir>]` | Clear state for a fresh re-provisioning |
| `validate --user-data <path>` | Validate cloud-config user-data without applying it |
| `collect-logs [--output <path>] [--state-dir <dir>]` | Bundle state + logs + scripts into a zip |
| `version` | Print agent version |

→ Full reference: [docs/user/reference/cli.md](docs/user/reference/cli.md)

---

## Finding VM IDs

```powershell
# Plain Hyper-V
Get-VM | Select-Object Name, Id

# eryph
Get-Catlet | Select-Object Name, VmId
```

---

## Troubleshooting

- **`get-status` returns `unknown`** — agent not running or Hyper-V integration services disabled in the VM.
- **SSH `Connection refused` / banner timeout** — the agent crashed; check `Get-EventLog -LogName Application -Source egs-service` in the VM.
- **Provisioning reports `failed`** — read `eryph.provisioning.error` via `egs-tool get-data --json <VM-ID>`; the agent also writes `%ProgramData%\eryph\provisioning\state.json` and per-script logs under `%ProgramData%\eryph\provisioning\logs\`.
- **Re-running provisioning** — `egs-service reset` (then reboot the VM, or use `egs-service run` for a one-shot synchronous run). See [docs/user/howto/reset-and-rerun.md](docs/user/howto/reset-and-rerun.md).

---

## Documentation

- **User docs**: [docs/user/](docs/user/index.md) — tutorials, how-to guides, reference, explanation.
- **RFCs**: [docs/rfcs/](docs/rfcs/README.md) — design decisions, including divergences from cloud-init / cloudbase-init.
- **Research notes**: [docs/research/](docs/research/) — Azure wireserver dissection, CustomData encryption verification.

---

## Development

```bash
dotnet build
dotnet test
dotnet pack
```

Solution layout:

```
src/Eryph.GuestServices.CloudConfig         POCOs + validators for #cloud-config
src/Eryph.GuestServices.CloudConfig.Yaml    YAML serialization
src/Eryph.GuestServices.Provisioning        the agent (library)
src/Eryph.GuestServices.Service             egs-service (guest binary)
src/Eryph.GuestServices.Tool                egs-tool (host binary)
src/Eryph.GuestServices.Sockets             Hyper-V socket abstraction
src/Eryph.GuestServices.Pty                 PTY support
test/                                       unit + integration + e2e tests
docs/                                       this documentation
```

---

## License

MIT — see [LICENSE](LICENSE).

## Support

- Issues: [GitHub Issues](https://github.com/eryph-org/guest-services/issues)
- eryph docs: [eryph.io/docs](https://eryph.io/docs)
- Community: [eryph Discussions](https://github.com/eryph-org/eryph/discussions)
