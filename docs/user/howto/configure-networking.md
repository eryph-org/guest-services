# Configure networking

Network configuration travels in a separate cloud-init document, network-config,
not in `#cloud-config`. The datasource supplies it and the `ApplyNetworkConfig`
module applies it during the Network stage. eryph supplies it as a **v1**
network-config on the NoCloud seed disk — a `cidata` volume holding `meta-data`,
`user-data`, and `network-config`.

Both the v1 and v2 schemas are accepted, and adapters are matched to the config
by MAC address.

## v1

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
        dns_search: [corp.example.com]
        routes:
          - network: 10.10.0.0/16
            gateway: 10.0.0.254
    mtu: 1500
  - type: physical
    name: eth1
    mac_address: 02:42:ac:11:00:03
    subnets:
      - type: dhcp4
```

## v2

```yaml
version: 2
ethernets:
  primary:
    match:
      macaddress: 02:42:ac:11:00:02
    addresses: [10.0.0.10/24, "fd00::10/64"]
    gateway4: 10.0.0.1
    gateway6: "fd00::1"
    nameservers:
      addresses: [10.0.0.53]
      search: [corp.example.com]
    routes:
      - to: 10.10.0.0/16
        via: 10.0.0.254
    mtu: 1500
  secondary:
    match:
      macaddress: 02:42:ac:11:00:03
    dhcp4: true
    dhcp6: true
```

## What's applied

IPv4 and IPv6 are handled independently. For a family set to DHCP, the agent
enables DHCP and leaves the rest of that family alone. For static addresses, it
disables DHCP for that family and sets the listed addresses and the gateway.
Per-interface routes, DNS servers, DNS search suffixes, and MTU are applied when
present. A family with no directive is left as-is.

Adapters are bound by MAC. An entry with no MAC is skipped with a warning —
Windows needs a MAC to bind to an adapter — as is an entry whose MAC matches no
adapter.

## Coverage matrix

The Windows applier implements the per-interface IP subset of each schema.
Anything outside that subset is **not** configured on the guest; where the
schema lets you express it, the agent makes the omission visible rather than
dropping it silently.

Legend: **Applied** — configured on the guest · **Warned** — parsed but not
applied, logged with a warning · **Not applied** — accepted but neither applied
nor warned.

### v2 (netplan-style)

| Construct | Status | Notes |
| --- | --- | --- |
| `match.macaddress` | Applied | the key used to bind the entry to an adapter |
| `match.name`, `match.driver` | Warned | Windows binds by MAC; an entry with no MAC is skipped |
| `dhcp4`, `dhcp6` | Applied | |
| `addresses` (IPv4 + IPv6) | Applied | static addresses; per-address `lifetime`/`label` options are ignored |
| `gateway4`, `gateway6` | Applied | |
| `routes` (`to`, `via`, `metric`) | Applied | includes `to: default`; `table`/`scope`/`type`/`on-link` are ignored |
| `nameservers.addresses`, `nameservers.search` | Applied | |
| `mtu` | Applied | |
| `macaddress` (set/spoof adapter MAC) | Warned | the adapter MAC is never changed |
| `dhcp4-overrides`, `dhcp6-overrides` | Warned | |
| `routing-policy` | Warned | |
| `set-name`, `wakeonlan`, `accept-ra` | Warned | |
| `optional` | Not applied | benign; no effect on Windows |
| `bonds`, `bridges`, `vlans` | Warned | the link is not created |
| `wifis`, `tunnels`, `vrfs`, … | Not applied | not modelled |
| `renderer` | Not applied | informational only |

### v1 (cloud-init networking config v1)

v1 is projected onto the same applier; only **IPv4 physical interfaces** are
projected.

| Construct | Status | Notes |
| --- | --- | --- |
| `type: physical` + `mac_address` | Applied | the key used to bind the entry to an adapter |
| `type: physical` without `mac_address` | Warned | skipped — needs a MAC to bind |
| `mtu` | Applied | |
| subnet `type: dhcp` / `dhcp4` | Applied | DHCPv4 (a physical entry with no subnets defaults to DHCP) |
| subnet `type: static` / `static4` (`address`, `gateway`) | Applied | IPv4 |
| `dns_nameservers`, `dns_search` | Applied | per-subnet, or inherited from a global `type: nameserver` entry |
| subnet `routes` (`network`, `gateway`, `metric`) | Applied | |
| `type: nameserver` (global DNS) | Applied | inherited by physical interfaces without their own DNS |
| subnet `type: static6` / `dhcp6` / `manual` | Not applied | v1 projects IPv4 only |
| `type: bond` / `bridge` / `vlan` / `route` | Not applied | only `physical` and `nameserver` entries are projected |

> v1 IPv6 subnets and the non-physical link types are currently dropped without
> a warning (unlike their v2 counterparts). If you need IPv6 or those link
> types on a v1 datasource, prefer the v2 schema.

`ApplyNetworkConfig` runs after `SetHostname`, so a hostname change isn't
disrupted by an address change in the same run. It's per-instance; to re-apply on
a running guest:

```powershell
egs-service reset
egs-service run --stage network
```
