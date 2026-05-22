# How to configure networking

Network configuration travels in a separate cloud-init document called
**network-config** (not part of `#cloud-config`). The datasource
exposes it; the `ApplyNetworkConfig` module reads it in the Network
stage.

On Hyper-V / eryph, eryph-zero writes the network-config into the
`config-2` ConfigDrive. On NoCloud setups, place a `network-config`
file next to `meta-data` / `user-data` on the cidata ISO.

## v1 schema (`version: 1`)

```yaml
version: 1
config:
  - type: physical
    name: eth0
    mac_address: 02:42:ac:11:00:02
    subnets:
      - type: static
        address: 10.0.0.10/24
        gateway: 10.0.0.1
        dns_nameservers: [10.0.0.53]
        mtu: 1500
  - type: physical
    name: eth1
    mac_address: 02:42:ac:11:00:03
    subnets:
      - type: dhcp4
```

## v2 schema (`version: 2`)

```yaml
version: 2
ethernets:
  primary:
    match:
      macaddress: 02:42:ac:11:00:02
    addresses: [10.0.0.10/24]
    gateway4: 10.0.0.1
    nameservers:
      addresses: [10.0.0.53]
    mtu: 1500
  secondary:
    match:
      macaddress: 02:42:ac:11:00:03
    dhcp4: true
```

Both schemas are accepted. Internally the v1 document is projected onto
the v2-shape `Ethernets` dictionary; either way, the agent matches each
entry to a Windows adapter by MAC address.

## What gets applied

For each matched adapter:

- **DHCP-only** (`dhcp4: true` and no `addresses`): DHCP is enabled,
  the IPv4 config is otherwise left alone.
- **Static**: DHCP is disabled, the listed `addresses` become the
  configured IPs, `gateway4` becomes the default gateway.
- **DNS** (`nameservers.addresses`): applied with
  `Set-DnsClientServerAddress` semantics.
- **MTU**: applied if `> 0`.

If `mac_address` is empty/missing, the entry is **skipped with a
warning** — Windows requires a MAC for adapter binding.

If the listed MAC has no matching adapter in the guest, the entry is
**skipped with a warning** (provisioning continues).

## What is not supported (v1)

- Bonds, bridges, VLANs (the schemas allow them; the applier doesn't).
- IPv6 addresses, routes, gateways.
- Wifi.

## Order vs hostname

`ApplyNetworkConfig` is `Stage.Network`, `Order = 2`. The hostname module
runs at `Order = 1` before it so an IP swap doesn't disconnect SSH
mid-rename. See [Stages reference](../reference/stages.md).

## Frequency

`per-instance`. To re-apply on a running guest:

```powershell
egs-service reset
egs-service run --stage network
```

See [RFC 0002](../../rfcs/0002-network-config-v1-v2-application.md) for the
design.
