# Eryph Guest Services

[![License](https://img.shields.io/github/license/eryph-org/guest-services.svg)](LICENSE)

Eryph Guest Services (EGS) is a small service that runs **inside** a Windows or Linux VM. One binary, two capabilities:

1. **A cloud-init-compatible provisioning agent** — reads `#cloud-config` user-data (NoCloud / ConfigDrive / Azure / Hyper-V KVP) on first boot and applies it (hostname, users, SSH keys, files, scripts, network).
2. **Userless SSH to the guest** — a dedicated SSH server you reach either over the **Hyper-V socket** (from the host, no IP/firewall/password) or, with eryph, over an **authorized channel through the eryph API** (from any machine, no host access).

EGS is the runtime baked into every [eryph](https://www.eryph.io) catlet, where both run together; either capability also works on its own.

---

## Pick your path

| Path | You have… | Start here |
|---|---|---|
| **eryph** | eryph catlets | [With eryph](#with-eryph-the-standard-path) |
| **Hyper-V** | a plain Hyper-V VM, no eryph | [Standalone on Hyper-V](#standalone-on-hyper-v) |
| **Other** | a non-eryph Windows VM needing cloud-init | [Windows cloud-init alternative](#windows-cloud-init-alternative-wip) |

### Components

| Binary | Where it runs | What it does |
|---|---|---|
| `egs-service` | Inside the VM (Windows service / systemd unit) | SSH server + provisioning agent |
| `egs-tool` | On the Hyper-V host, **or** (for the `eryph` subcommands) any machine with an eryph connection | SSH config writer, file transfer, status, key exchange |

---

## With eryph (the standard path)

eryph bakes EGS into every catlet, so there is nothing to install. Provisioning and SSH access are part of the normal catlet workflow.

### Provisioning

eryph injects a ConfigDrive ISO into every catlet at create time; the EGS agent applies that cloud-config fodder on first boot and reports `eryph.provisioning.state = completed` via Hyper-V KVP. The fodder normally comes from your **catlet spec, packs, or genes** — so for the eryph path provisioning "just happens", no EGS configuration involved.


The default is to compose catlets from specs/packs/genes. When you are authoring or testing directly, you can also create one from an inline spec (its fodder included):

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

→ Full walkthrough: [Tutorial: first catlet with cloud-config](docs/user/tutorial/first-catlet-with-cloud-config.md).

→ [What the provisioning agent supports](#what-the-provisioning-agent-supports)

### Remote SSH access

Reach a catlet's guest SSH **from any machine** — the `egs-tool eryph` commands tunnel the same guest SSH server through eryph's authorized compute API (the bytes are relayed host-side over the Hyper-V socket; SSH runs end-to-end). No Hyper-V host access and **no administrator rights** are needed: the commands run as the operator and authenticate with the operator's eryph identity. The eryph client must hold the `compute:catlets:remote-access` scope.

```powershell
# Write an SSH alias for a catlet that tunnels through eryph
egs-tool eryph add-ssh-config <catlet-id>

# Connect (the alias's ProxyCommand bridges through eryph)
ssh <catlet>.<project>.eryph.alt
ssh <catlet>.eryph.alt          # short form, for the 'default' project
ssh <catlet-id>.eryph.alt       # canonical, always unique
```

**Keys.** By default the alias uses a **per-user managed key**, created on demand and stored in your own profile with a user-only ACL (so Windows OpenSSH will load it). Bring your own key with `--identity`:

```powershell
egs-tool eryph add-ssh-config <catlet-id> --identity C:\Users\me\.ssh\id_ed25519
```

The public key still has to be authorized in the guest — pre-inject it at build time, or push it at runtime.

**Build time** — set the `sshPublicKey` variable of the [`dbosoft/guest-services`](https://genepool.eryph.io/b/dbosoft/guest-services) install gene in the catlet spec. Use the output of `egs-tool eryph get-client-key`:

```yaml
name: my-vm
parent: dbosoft/winsrv2022-standard/starter
fodder:
  - source: gene:dbosoft/guest-services:win-install   # use linux-install on Linux
    variables:
      - name: sshPublicKey
        value: 'ecdsa-sha2-nistp256 AAAA... egs'        # from: egs-tool eryph get-client-key
```

**Runtime** — authorize a key on a running catlet:

```powershell
# Authorize the alias's key in the guest immediately
egs-tool eryph add-ssh-config <catlet-id> --add-key

# Or manage authorized keys directly (optionally with an expiry)
egs-tool eryph add-key    <catlet-id> [--public-key <path|->] [--ttl 8h]
egs-tool eryph remove-key <catlet-id>
```

Select a specific eryph client / configuration with `--client-id <id>` and `--configuration <name>`; otherwise the default eryph connection is used.

> On the Hyper-V host you can also reach the catlet over the Hyper-V socket, exactly like a standalone VM — see [Standalone on Hyper-V](#standalone-on-hyper-v). File transfer (`upload-file` / `download-file`) uses that socket transport.

---

## Standalone on Hyper-V

You have a Hyper-V VM (no eryph) and want to talk to it without setting up networking.

### Install

Host tool:

```powershell
iex ((New-Object System.Net.WebClient).DownloadString('https://raw.githubusercontent.com/eryph-org/guest-services/main/src/Eryph.GuestServices.Tool/install.ps1'))
egs-tool initialize
```

Guest — download the installer ISO from [releases.dbosoft.eu/eryph/guest-services/](https://releases.dbosoft.eu/eryph/guest-services/), mount it in the VM, then:

```powershell
# Windows guest, as Administrator
D:\install.ps1
```

```bash
# Linux guest, as root
sudo /media/cdrom/install.sh
```

### Userless SSH over the Hyper-V socket

The guest exposes a dedicated SSH server on the Hyper-V socket (service ID `0000138a-facb-11e6-bd58-64006a7986d3` / Linux vsock port 5002). The host tool writes an SSH config that uses `egs-tool.exe proxy <vmid>` as the proxy command, and key exchange happens through Hyper-V KVP — no manual `ssh-copy-id`.

```powershell
# Check whether the guest agent is reachable
egs-tool get-status <VM-ID>

# Add a single VM to the SSH config
egs-tool add-ssh-config <VM-ID> [alias]

# Connect
ssh <VM-ID>.hyper-v.alt   # by VM id
ssh <alias>               # if an alias was given
```

No IP, no firewall, no password.

#### Configure the shell

By default interactive sessions land in `powershell.exe` (Windows) or `$SHELL` / `/bin/bash` (Linux). Override per-VM:

```powershell
egs-tool set-shell <VM-ID> --command pwsh.exe
egs-tool set-shell <VM-ID> --command pwsh.exe --arguments="-NoLogo -NoProfile"
egs-tool set-shell <VM-ID> --reset
```

SSH clients can also send `SHELL` / `SHELL_ARGS` env vars; the per-VM override always wins.

#### File transfer

```powershell
egs-tool upload-file        <VM-ID> <local> <remote>  [--overwrite]
egs-tool upload-directory   <VM-ID> <local> <remote>  [--recursive] [--overwrite]
egs-tool download-file      <VM-ID> <remote> <local>  [--overwrite]
egs-tool download-directory <VM-ID> <remote> <local>  [--recursive] [--overwrite]
```

No `scp` — the channel is built on the same SSH-over-Hyper-V-socket transport. For interactive sync, use `unison` over the SSH tunnel.

### Provisioning (optional)

The provisioning agent ships in the same binary but you don't have to use it — your VM is already configured by whatever you used to build it. It only runs if you feed it a datasource (NoCloud ConfigDrive ISO, Hyper-V KVP user-data, etc.); see the [user docs](docs/user/index.md) and [What the provisioning agent supports](#what-the-provisioning-agent-supports).

---

## Windows cloud-init alternative (WIP)

EGS as a general-purpose replacement for cloudbase-init on Windows VMs outside eryph (OpenStack, custom KVM, hand-rolled NoCloud images). **This is work in progress and not fully supported yet.** The core RFCs (network-config, frequencies, semaphores, scripts, datasource lifecycle) are landed; the longer tail (jinja2, part-handler, boothook, OpenStack-specific quirks) is not. See [Windows cloud-init status](docs/user/explanation/windows-cloud-init-status.md) before relying on this in production.

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

## Why this exists

- Windows Server 2025 ships OpenSSH but [still no Hyper-V socket support](https://github.com/PowerShell/Win32-OpenSSH/issues/2200).
- [PowerShell Direct](https://learn.microsoft.com/en-us/powershell/scripting/learn/remoting/ps-direct) is Windows-only and has poor file-transfer performance.
- Linux vsock SSH is distro-dependent.
- For provisioning: cloudbase-init has known bugs (requires `filename=` in multipart MIME, ignores shebangs, several Python 2 / Windows-encoding traps). EGS is .NET-native, byte-clean, and ships fixes for these. See [differences from cloudbase-init](docs/user/explanation/differences-from-cloudbase-init.md).

---

## Requirements

**Host**: Windows 10/11 Pro/Enterprise/Education or Windows Server 2016+, Hyper-V enabled, administrator privileges. (The `egs-tool eryph` subcommands instead run on any machine with an eryph connection, without admin.)

**Guest**: Windows Server 2016+ / Windows 10+ or a modern Linux distribution with systemd. Hyper-V integration services enabled.

---

## Authentication

The guest SSH server uses public-key authentication only — passwords are disabled — with the fixed username `egs`. Host keys are trusted automatically because the transport is isolated (the Hyper-V socket) or authorized end-to-end (the eryph channel). How the key reaches the guest differs by path:

- **Hyper-V socket flow** — the host key is exchanged via the Hyper-V data-exchange service (KVP); `egs-tool initialize` generates the admin/SYSTEM-scoped host key.

  ```powershell
  egs-tool get-ssh-key   # show the host-side public key
  egs-tool initialize    # regenerate keys if needed
  ```

- **eryph remote flow** — the operator's key is authorized through the compute API. The default is a per-user managed key (user-only ACL, created on demand); `egs-tool eryph get-client-key` prints its public key for fodder pre-injection, and `--identity` selects your own key. See [Remote SSH access](#remote-ssh-access).

---

## CLI quick reference

### `egs-tool` (Hyper-V host)

| Command | What |
|---|---|
| `initialize` | Register Hyper-V service + generate SSH keys |
| `unregister` | Remove the Hyper-V integration service |
| `get-status <VM-ID>` | Check whether the guest agent is reachable |
| `get-ssh-key` | Print the host's SSH public key |
| `add-ssh-config <VM-ID> [alias]` | Add one VM to the SSH config (Hyper-V socket) |
| `upload-file` / `upload-directory` | Push file(s) to a VM |
| `download-file` / `download-directory` | Pull file(s) from a VM |
| `set-shell <VM-ID>` | Override the interactive shell |
| `get-data --json <VM-ID>` | Dump Hyper-V KVP for the VM |

Flags: `--overwrite`, `--recursive` (directory commands), `--reset` (`set-shell`).

### `egs-tool eryph` (remote access over eryph)

Run on any machine with an eryph connection — not just the Hyper-V host — and authenticate with the operator's eryph identity (no admin rights).

| Command | What |
|---|---|
| `eryph add-ssh-config <catlet-id> [--identity <key>] [--add-key]` | Write an SSH alias that tunnels through eryph |
| `eryph add-key <catlet-id> [--public-key <path\|->] [--ttl <dur>]` | Authorize a public key in the guest (optionally expiring) |
| `eryph remove-key <catlet-id>` | Revoke the caller's authorized key |
| `eryph get-client-key` | Print the per-user managed public key (for fodder pre-injection) |

Common flags: `--client-id <id>`, `--configuration <name>` to select the eryph connection. Requires the `compute:catlets:remote-access` scope.

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

## Finding IDs

```powershell
# Plain Hyper-V — VM ids
Get-VM | Select-Object Name, Id

# eryph — catlet ids (and VM ids)
Get-Catlet | Select-Object Name, Id, VmId
```

The `egs-tool eryph` commands take a **catlet id**; the Hyper-V socket commands take a **VM id**.

---

## Troubleshooting

- **`get-status` returns `unknown`** — agent not running or Hyper-V integration services disabled in the VM.
- **SSH `Connection refused` / banner timeout** — the agent crashed; check `Get-EventLog -LogName Application -Source egs-service` in the VM.
- **`egs-tool eryph` cannot connect** — confirm an eryph connection is configured (or eryph-zero is running) and the client has the `compute:catlets:remote-access` scope; select a specific one with `--configuration` / `--client-id`.
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
</content>
</invoke>
