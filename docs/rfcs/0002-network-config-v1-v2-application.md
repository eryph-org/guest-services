# RFC 0002 — Network-config v1/v2 application on Windows

Status: Draft

## Problem

Cloud-init defines two network-config schemas (v1 and v2). Eryph-zero emits network config in one of these (typically v2). On Windows, applying network-config means setting static IPs, DNS, MTU, routes, and possibly disabling DHCP per adapter. We need a Windows applier that understands the schema.

## What cloud-init does

On Linux, cloud-init renders network-config into the distro's native format: `/etc/systemd/network/`, `/etc/network/interfaces`, netplan YAML, NetworkManager keyfile, etc. The `renderer` field in the input selects which (or auto-detects). Application is delegated to the OS — cloud-init just writes the config file.

## What cloudbase-init does

Reads only the older "OpenStack-style" network metadata; doesn't fully support cloud-init network-config v2. Applies via WMI / netsh.

## Eryph context

- Eryph-zero ships static IPs, DNS, MTU for Windows VMs.
- Adapter matching on Windows is by MAC address (cloud-init v2 supports this).
- VLAN, bond, bridge usage is rare on Windows guests; deprioritize.

## Options

1. **PowerShell (NetTCPIP module).** `New-NetIPAddress`, `Set-DnsClientServerAddress`, etc. Easy to write, slow to invoke per call.
2. **netsh.** Older, scriptable, faster startup than PowerShell.
3. **WMI / CIM (`MSFT_NetIPInterface`, `MSFT_DNSClientServerAddress`).** Most direct, requires p/invoke or `Microsoft.Management.Infrastructure`.
4. **A blend** — WMI for read/discovery, PowerShell for writes (since it has better idempotency semantics).

## Tentative direction

**Hybrid: CIM for discovery + PowerShell for application.** Reasoning: CIM is fast for enumerating adapters and matching by MAC; PowerShell's NetTCPIP cmdlets are idempotent and well-tested.

v1 scope: support ethernet adapters with `dhcp4`, `addresses`, `gateway4`, `nameservers`, `mtu`. Defer bonds/bridges/vlans to v2.

## Open questions

- DHCP coexistence: what if eryph-zero specifies static for one adapter but other adapters were configured via DHCP on first boot? Leave them alone, or normalize?
- Order of operations vs the rest of provisioning. Network change might disconnect SSH; should the agent reapply on every run or only when config changed?
- v1 schema is structurally different from v2. Worth supporting both? Cloud-init does. For eryph-zero we probably only need v2.
