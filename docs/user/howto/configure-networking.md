# Configure networking

Network configuration travels in a separate cloud-init document, network-config,
not in `#cloud-config`. The datasource supplies it and the `ApplyNetworkConfig`
module applies it during the Network stage. On eryph it's written into the
`config-2` ConfigDrive; on a NoCloud setup, put a `network-config` file next to
`meta-data` and `user-data`.

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

An entry with no MAC address is skipped with a warning — Windows needs a MAC to
bind to an adapter — as is an entry whose MAC matches no adapter.

Bonds, bridges, VLANs, and wifi aren't applied, even though the schemas allow
them — when present they're logged with a warning rather than dropped silently.
The same goes for per-interface options the agent doesn't honour on Windows
(`dhcp4-overrides`/`dhcp6-overrides`, `routing-policy`, `set-name`, `wakeonlan`,
`accept-ra`) and for a top-level `macaddress` that asks to change an adapter's
MAC: the rest of the entry is still applied, and the ignored parts are warned.

`ApplyNetworkConfig` runs after `SetHostname`, so a hostname change isn't
disrupted by an address change in the same run. It's per-instance; to re-apply on
a running guest:

```powershell
egs-service reset
egs-service run --stage network
```
