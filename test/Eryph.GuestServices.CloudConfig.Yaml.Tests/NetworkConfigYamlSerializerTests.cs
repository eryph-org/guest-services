using AwesomeAssertions;
using Eryph.GuestServices.CloudConfig;
using Eryph.GuestServices.CloudConfig.Yaml;

namespace Eryph.GuestServices.CloudConfig.Yaml.Tests;

public class NetworkConfigYamlSerializerTests
{
    [Fact]
    public void Deserialize_empty_input_returns_empty_network_config()
    {
        var result = NetworkConfigYamlSerializer.Deserialize("");

        result.Should().NotBeNull();
        result.Version.Should().Be(0);
        result.Ethernets.Should().BeNull();
    }

    [Fact]
    public void Deserialize_v2_ethernet_with_dhcp4_and_nameservers()
    {
        const string yaml = """
                            version: 2
                            ethernets:
                              eth0:
                                dhcp4: true
                                addresses:
                                  - 10.0.0.5/24
                                gateway4: 10.0.0.1
                                nameservers:
                                  addresses:
                                    - 1.1.1.1
                                    - 8.8.8.8
                                  search:
                                    - example.com
                                mtu: 1500
                            """;

        var result = NetworkConfigYamlSerializer.Deserialize(yaml);

        result.Version.Should().Be(2);
        result.Ethernets.Should().NotBeNull().And.ContainKey("eth0");

        var eth0 = result.Ethernets!["eth0"];
        eth0.Dhcp4.Should().BeTrue();
        eth0.Addresses.Should().BeEquivalentTo(["10.0.0.5/24"]);
        eth0.Gateway4.Should().Be("10.0.0.1");
        eth0.Mtu.Should().Be(1500);
        eth0.Nameservers.Should().NotBeNull();
        eth0.Nameservers!.Addresses.Should().BeEquivalentTo(["1.1.1.1", "8.8.8.8"]);
        eth0.Nameservers!.Search.Should().BeEquivalentTo(["example.com"]);
    }

    [Fact]
    public void Deserialize_v2_ethernet_with_macaddress_and_routes()
    {
        const string yaml = """
                            version: 2
                            ethernets:
                              eth0:
                                macaddress: "00:11:22:33:44:55"
                                routes:
                                  - to: default
                                    via: 10.0.0.1
                                    metric: 100
                            """;

        var result = NetworkConfigYamlSerializer.Deserialize(yaml);

        var eth0 = result.Ethernets!["eth0"];
        eth0.MacAddress.Should().Be("00:11:22:33:44:55");
        eth0.Routes.Should().ContainSingle();
        eth0.Routes![0].To.Should().Be("default");
        eth0.Routes![0].Via.Should().Be("10.0.0.1");
        eth0.Routes![0].Metric.Should().Be(100);
    }

    [Fact]
    public void Deserialize_v2_with_renderer_networkd()
    {
        const string yaml = """
                            version: 2
                            renderer: networkd
                            ethernets: {}
                            """;

        var result = NetworkConfigYamlSerializer.Deserialize(yaml);

        result.Renderer.Should().Be(NetworkDhcpRenderer.Networkd);
    }

    [Fact]
    public void Deserialize_v2_with_renderer_network_manager()
    {
        const string yaml = """
                            version: 2
                            renderer: NetworkManager
                            """;

        var result = NetworkConfigYamlSerializer.Deserialize(yaml);

        result.Renderer.Should().Be(NetworkDhcpRenderer.NetworkManager);
    }

    [Fact]
    public void Deserialize_v2_with_bond_and_bridge()
    {
        const string yaml = """
                            version: 2
                            bonds:
                              bond0:
                                interfaces:
                                  - eth1
                                  - eth2
                                parameters:
                                  mode: active-backup
                            bridges:
                              br0:
                                interfaces:
                                  - eth3
                            """;

        var result = NetworkConfigYamlSerializer.Deserialize(yaml);

        result.Bonds.Should().ContainKey("bond0");
        result.Bonds!["bond0"].Interfaces.Should().BeEquivalentTo(["eth1", "eth2"]);
        result.Bonds!["bond0"].Parameters.Should().Contain("mode", "active-backup");

        result.Bridges.Should().ContainKey("br0");
        result.Bridges!["br0"].Interfaces.Should().BeEquivalentTo(["eth3"]);
    }

    [Fact]
    public void Deserialize_v2_with_vlan()
    {
        const string yaml = """
                            version: 2
                            vlans:
                              vlan100:
                                link: eth0
                                id: 100
                            """;

        var result = NetworkConfigYamlSerializer.Deserialize(yaml);

        result.Vlans.Should().ContainKey("vlan100");
        result.Vlans!["vlan100"].Link.Should().Be("eth0");
        result.Vlans!["vlan100"].Id.Should().Be(100);
    }

    [Fact]
    public void Deserialize_v2_match_macaddress_block_is_parsed()
    {
        // Real cloud-init/netplan binds an interface to a NIC via a `match`
        // sub-map, NOT the top-level `macaddress` (which SETS/spoofs a MAC).
        // Regression for issue #59: a `match:` mapping previously threw because
        // Match was modelled as a string, and the swallowed exception left the
        // whole network-config null.
        const string yaml = """
                            version: 2
                            ethernets:
                              eth0:
                                match:
                                  macaddress: "00:11:22:aa:bb:cc"
                                addresses:
                                  - 10.0.0.5/24
                            """;

        var result = NetworkConfigYamlSerializer.Deserialize(yaml);

        var eth0 = result.Ethernets!["eth0"];
        eth0.Match.Should().NotBeNull();
        eth0.Match!.MacAddress.Should().Be("00:11:22:aa:bb:cc");
        eth0.Addresses.Should().BeEquivalentTo(["10.0.0.5/24"]);
    }

    [Fact]
    public void Deserialize_v2_match_name_and_driver_are_parsed()
    {
        const string yaml = """
                            version: 2
                            ethernets:
                              eth0:
                                match:
                                  name: "en*"
                                  driver: "e1000"
                            """;

        var result = NetworkConfigYamlSerializer.Deserialize(yaml);

        var match = result.Ethernets!["eth0"].Match;
        match.Should().NotBeNull();
        match!.Name.Should().Be("en*");
        match.Driver.Should().Be("e1000");
        match.MacAddress.Should().BeNull();
    }

    [Fact]
    public void Deserialize_v2_flow_style_from_issue_59()
    {
        // Flow-style mappings and sequences (as a generator may emit them),
        // exercising the issue #59 match-block path end to end.
        const string yaml = """
                            ethernets:
                              eth0:
                                addresses: [10.0.0.5/24]
                                gateway4: 10.0.0.1
                                match: {macaddress: '00:11:22:aa:bb:cc'}
                                nameservers:
                                  addresses: [10.0.0.1]
                            version: 2
                            """;

        var result = NetworkConfigYamlSerializer.Deserialize(yaml);

        result.Version.Should().Be(2);
        var eth0 = result.Ethernets!["eth0"];
        eth0.Match!.MacAddress.Should().Be("00:11:22:aa:bb:cc");
        eth0.Addresses.Should().BeEquivalentTo(["10.0.0.5/24"]);
        eth0.Gateway4.Should().Be("10.0.0.1");
        eth0.Nameservers!.Addresses.Should().BeEquivalentTo(["10.0.0.1"]);
    }

    [Fact]
    public void Deserialize_v2_dhcp_overrides_blocks_are_tolerated()
    {
        // dhcp4-overrides / dhcp6-overrides are sub-maps we don't model. They
        // must be ignored without aborting the parse — the issue-59 failure
        // mode was a known key with an unexpected shape killing the document.
        const string yaml = """
                            version: 2
                            ethernets:
                              eth0:
                                macaddress: "00:11:22:33:44:55"
                                dhcp4: true
                                dhcp4-overrides:
                                  route-metric: 200
                                  use-dns: false
                                dhcp6-overrides:
                                  use-dns: false
                            """;

        var result = NetworkConfigYamlSerializer.Deserialize(yaml);

        var eth0 = result.Ethernets!["eth0"];
        eth0.Dhcp4.Should().BeTrue();
        eth0.MacAddress.Should().Be("00:11:22:33:44:55");
        // The drained override blocks must not leak into sibling fields.
        eth0.Dhcp6.Should().BeNull();
        eth0.Gateway4.Should().BeNull();
        eth0.Addresses.Should().BeNull();
        eth0.Routes.Should().BeNull();
        // ...but their presence is surfaced so the applier can warn.
        eth0.UnsupportedOptions.Should().BeEquivalentTo(["dhcp4-overrides", "dhcp6-overrides"]);
    }

    [Fact]
    public void Deserialize_v2_unmodelled_scalar_keys_are_tolerated()
    {
        // set-name / wakeonlan / optional / accept-ra are valid netplan keys
        // the Windows applier does not act on. They must not break parsing, and
        // the noteworthy ones are surfaced via UnsupportedOptions so the applier
        // can warn (optional is benign and intentionally not surfaced).
        const string yaml = """
                            version: 2
                            ethernets:
                              eth0:
                                match:
                                  macaddress: "00:11:22:33:44:55"
                                set-name: eth0
                                wakeonlan: true
                                optional: true
                                accept-ra: false
                                dhcp4: true
                            """;

        var result = NetworkConfigYamlSerializer.Deserialize(yaml);

        var eth0 = result.Ethernets!["eth0"];
        eth0.Match!.MacAddress.Should().Be("00:11:22:33:44:55");
        eth0.Dhcp4.Should().BeTrue();
        eth0.UnsupportedOptions.Should().BeEquivalentTo(["set-name", "wakeonlan", "accept-ra"]);
    }

    [Fact]
    public void Deserialize_v2_dhcp_bool_yes_no_forms()
    {
        // YAML 1.1 / PyYAML bool tokens: cloud-init accepts yes/no/on/off.
        const string yaml = """
                            version: 2
                            ethernets:
                              eth0:
                                macaddress: "00:11:22:33:44:55"
                                dhcp4: no
                                dhcp6: yes
                            """;

        var result = NetworkConfigYamlSerializer.Deserialize(yaml);

        var eth0 = result.Ethernets!["eth0"];
        eth0.Dhcp4.Should().BeFalse();
        eth0.Dhcp6.Should().BeTrue();
    }

    [Fact]
    public void Deserialize_v2_route_with_unmodelled_fields_is_tolerated()
    {
        const string yaml = """
                            version: 2
                            ethernets:
                              eth0:
                                macaddress: "00:11:22:33:44:55"
                                routes:
                                  - to: 10.10.0.0/16
                                    via: 10.0.0.1
                                    metric: 100
                                    on-link: true
                            """;

        var result = NetworkConfigYamlSerializer.Deserialize(yaml);

        var route = result.Ethernets!["eth0"].Routes!.Single();
        route.To.Should().Be("10.10.0.0/16");
        route.Via.Should().Be("10.0.0.1");
        route.Metric.Should().Be(100);
    }

    [Fact]
    public void Deserialize_v2_ethernet_without_match_leaves_match_null()
    {
        const string yaml = """
                            version: 2
                            ethernets:
                              eth0:
                                macaddress: "00:11:22:33:44:55"
                                dhcp4: true
                            """;

        var result = NetworkConfigYamlSerializer.Deserialize(yaml);

        result.Ethernets!["eth0"].Match.Should().BeNull();
    }

    [Fact]
    public void Deserialize_v2_match_combines_name_macaddress_and_driver()
    {
        const string yaml = """
                            version: 2
                            ethernets:
                              eth0:
                                match:
                                  name: "eth*"
                                  macaddress: "00:11:22:33:44:55"
                                  driver: "virtio_net"
                                dhcp4: true
                            """;

        var result = NetworkConfigYamlSerializer.Deserialize(yaml);

        var match = result.Ethernets!["eth0"].Match!;
        match.Name.Should().Be("eth*");
        match.MacAddress.Should().Be("00:11:22:33:44:55");
        match.Driver.Should().Be("virtio_net");
    }

    [Fact]
    public void Deserialize_v2_static_dualstack_with_both_gateways()
    {
        const string yaml = """
                            version: 2
                            ethernets:
                              eth0:
                                macaddress: "00:11:22:33:44:55"
                                addresses:
                                  - 10.0.0.5/24
                                  - "fd00::5/64"
                                gateway4: 10.0.0.1
                                gateway6: "fd00::1"
                            """;

        var result = NetworkConfigYamlSerializer.Deserialize(yaml);

        var eth0 = result.Ethernets!["eth0"];
        eth0.Addresses.Should().BeEquivalentTo(["10.0.0.5/24", "fd00::5/64"]);
        eth0.Gateway4.Should().Be("10.0.0.1");
        eth0.Gateway6.Should().Be("fd00::1");
    }

    [Fact]
    public void Deserialize_v2_dhcp4_and_dhcp6_both_true()
    {
        const string yaml = """
                            version: 2
                            ethernets:
                              eth0:
                                macaddress: "00:11:22:33:44:55"
                                dhcp4: true
                                dhcp6: true
                            """;

        var result = NetworkConfigYamlSerializer.Deserialize(yaml);

        var eth0 = result.Ethernets!["eth0"];
        eth0.Dhcp4.Should().BeTrue();
        eth0.Dhcp6.Should().BeTrue();
    }

    [Fact]
    public void Deserialize_v2_multiple_ethernets_in_one_document()
    {
        const string yaml = """
                            version: 2
                            ethernets:
                              primary:
                                match:
                                  macaddress: "00:11:22:33:44:01"
                                addresses: [10.0.0.5/24]
                              secondary:
                                match:
                                  macaddress: "00:11:22:33:44:02"
                                dhcp4: true
                            """;

        var result = NetworkConfigYamlSerializer.Deserialize(yaml);

        result.Ethernets.Should().HaveCount(2);
        result.Ethernets!["primary"].Match!.MacAddress.Should().Be("00:11:22:33:44:01");
        result.Ethernets!["primary"].Addresses.Should().BeEquivalentTo(["10.0.0.5/24"]);
        result.Ethernets!["secondary"].Match!.MacAddress.Should().Be("00:11:22:33:44:02");
        result.Ethernets!["secondary"].Dhcp4.Should().BeTrue();
    }

    [Fact]
    public void Deserialize_v2_network_wrapper_is_equivalent_to_bare_form()
    {
        // netplan / many cloud-init samples nest everything under a top-level
        // `network:` key. It must parse identically to the bare form (which it
        // previously did not — the wrapper produced an empty config).
        const string wrapped = """
                            network:
                              version: 2
                              ethernets:
                                eth0:
                                  match:
                                    macaddress: "00:11:22:33:44:55"
                                  addresses: [10.0.0.5/24]
                                  gateway4: 10.0.0.1
                            """;

        var result = NetworkConfigYamlSerializer.Deserialize(wrapped);

        result.Version.Should().Be(2);
        var eth0 = result.Ethernets!["eth0"];
        eth0.Match!.MacAddress.Should().Be("00:11:22:33:44:55");
        eth0.Addresses.Should().BeEquivalentTo(["10.0.0.5/24"]);
        eth0.Gateway4.Should().Be("10.0.0.1");
    }

    [Fact]
    public void Deserialize_v1_network_wrapper_is_unwrapped()
    {
        const string yaml = """
                            network:
                              version: 1
                              config:
                                - type: physical
                                  name: eth0
                                  mac_address: 00:11:22:33:44:55
                                  subnets:
                                    - type: dhcp
                            """;

        var result = NetworkConfigYamlSerializer.Deserialize(yaml);

        result.Version.Should().Be(1);
        result.Ethernets!["eth0"].Dhcp4.Should().BeTrue();
        result.Ethernets!["eth0"].MacAddress.Should().Be("00:11:22:33:44:55");
    }

    [Fact]
    public void Deserialize_v2_advanced_address_map_form_keeps_the_address()
    {
        // netplan's advanced form attaches per-address options via a single-key
        // map. We keep the address (the key) and drop label/lifetime. The map
        // form previously threw and nulled the whole config.
        const string yaml = """
                            version: 2
                            ethernets:
                              eth0:
                                macaddress: "00:11:22:33:44:55"
                                addresses:
                                  - 10.0.0.5/24
                                  - "fd00::5/64":
                                      lifetime: 0
                                      label: "maas"
                            """;

        var result = NetworkConfigYamlSerializer.Deserialize(yaml);

        result.Ethernets!["eth0"].Addresses
            .Should().BeEquivalentTo(["10.0.0.5/24", "fd00::5/64"]);
    }

    [Fact]
    public void Deserialize_v2_routing_policy_block_is_tolerated()
    {
        const string yaml = """
                            version: 2
                            ethernets:
                              eth0:
                                macaddress: "00:11:22:33:44:55"
                                addresses: [10.0.0.5/24]
                                routing-policy:
                                  - from: 10.0.0.0/24
                                    table: 101
                                    priority: 100
                            """;

        var result = NetworkConfigYamlSerializer.Deserialize(yaml);

        var eth0 = result.Ethernets!["eth0"];
        eth0.Addresses.Should().BeEquivalentTo(["10.0.0.5/24"]);
        // routing-policy is dropped, not misread into modelled routes...
        eth0.Routes.Should().BeNull();
        // ...and surfaced so the applier can warn.
        eth0.UnsupportedOptions.Should().BeEquivalentTo(["routing-policy"]);
    }

    [Fact]
    public void Deserialize_v2_bond_with_parameters_and_addressing_is_tolerated()
    {
        // Bonds aren't applied on Windows, but a bond carrying addressing keys
        // and a parameters map must still parse (and be preserved as a bond).
        const string yaml = """
                            version: 2
                            bonds:
                              bond0:
                                interfaces: [eth0, eth1]
                                parameters:
                                  mode: 802.3ad
                                  mii-monitor-interval: 100
                                addresses: [10.0.0.9/24]
                                dhcp4: false
                                nameservers:
                                  addresses: [1.1.1.1]
                            """;

        var result = NetworkConfigYamlSerializer.Deserialize(yaml);

        result.Bonds.Should().ContainKey("bond0");
        result.Bonds!["bond0"].Interfaces.Should().BeEquivalentTo(["eth0", "eth1"]);
        result.Bonds!["bond0"].Parameters.Should().ContainKey("mode");
    }

    [Fact]
    public void Deserialize_v2_bridge_with_parameters_and_addressing_is_tolerated()
    {
        const string yaml = """
                            version: 2
                            bridges:
                              br0:
                                interfaces: [eth0]
                                parameters:
                                  stp: true
                                  forward-delay: 12
                                addresses: [10.0.0.2/24]
                                dhcp4: true
                            """;

        var result = NetworkConfigYamlSerializer.Deserialize(yaml);

        result.Bridges.Should().ContainKey("br0");
        result.Bridges!["br0"].Interfaces.Should().BeEquivalentTo(["eth0"]);
    }

    [Fact]
    public void Deserialize_v2_vlan_with_addressing_is_tolerated()
    {
        const string yaml = """
                            version: 2
                            vlans:
                              vlan100:
                                id: 100
                                link: eth0
                                addresses: [10.0.100.5/24]
                                gateway4: 10.0.100.1
                            """;

        var result = NetworkConfigYamlSerializer.Deserialize(yaml);

        result.Vlans.Should().ContainKey("vlan100");
        result.Vlans!["vlan100"].Id.Should().Be(100);
        result.Vlans!["vlan100"].Link.Should().Be("eth0");
    }

    [Fact]
    public void Deserialize_v2_kitchen_sink_document_parses_all_modelled_fields()
    {
        // A single document exercising the full v2 surface at once: every
        // device type, the renderer, match selectors, both IP families, DNS,
        // routes, MTU, and a pile of keys we tolerate but don't apply
        // (set-name, wakeonlan, optional, accept-ra, dhcp overrides,
        // routing-policy). The whole thing must parse without throwing and
        // populate every field the applier consumes.
        const string yaml = """
                            network:
                              version: 2
                              renderer: networkd
                              ethernets:
                                primary:
                                  match:
                                    name: "eth*"
                                    macaddress: "00:11:22:33:44:55"
                                    driver: "virtio_net"
                                  set-name: primary
                                  wakeonlan: true
                                  optional: true
                                  accept-ra: false
                                  dhcp4: false
                                  dhcp6: false
                                  dhcp4-overrides:
                                    route-metric: 200
                                  addresses:
                                    - 10.0.0.5/24
                                    - "fd00::5/64":
                                        lifetime: 0
                                  gateway4: 10.0.0.1
                                  gateway6: "fd00::1"
                                  nameservers:
                                    addresses: [1.1.1.1, "2606:4700:4700::1111"]
                                    search: [corp.local, example.com]
                                  routes:
                                    - to: 10.10.0.0/16
                                      via: 10.0.0.254
                                      metric: 100
                                      on-link: true
                                    - to: default
                                      via: 10.0.0.1
                                  routing-policy:
                                    - from: 10.0.0.0/24
                                      table: 101
                                  mtu: 9000
                                  macaddress: "00:11:22:33:44:55"
                                fallback:
                                  match:
                                    macaddress: "00:11:22:33:44:66"
                                  dhcp4: true
                              bonds:
                                bond0:
                                  interfaces: [eth2, eth3]
                                  parameters:
                                    mode: active-backup
                              bridges:
                                br0:
                                  interfaces: [bond0]
                                  parameters:
                                    stp: true
                              vlans:
                                vlan100:
                                  id: 100
                                  link: br0
                            """;

        var result = NetworkConfigYamlSerializer.Deserialize(yaml);

        result.Version.Should().Be(2);
        result.Renderer.Should().Be(NetworkDhcpRenderer.Networkd);

        var primary = result.Ethernets!["primary"];
        primary.Match!.Name.Should().Be("eth*");
        primary.Match.MacAddress.Should().Be("00:11:22:33:44:55");
        primary.Match.Driver.Should().Be("virtio_net");
        primary.Dhcp4.Should().BeFalse();
        primary.Dhcp6.Should().BeFalse();
        primary.Addresses.Should().BeEquivalentTo(["10.0.0.5/24", "fd00::5/64"]);
        primary.Gateway4.Should().Be("10.0.0.1");
        primary.Gateway6.Should().Be("fd00::1");
        primary.Nameservers!.Addresses.Should().BeEquivalentTo(["1.1.1.1", "2606:4700:4700::1111"]);
        primary.Nameservers!.Search.Should().BeEquivalentTo(["corp.local", "example.com"]);
        primary.Routes.Should().HaveCount(2);
        primary.Routes![0].To.Should().Be("10.10.0.0/16");
        primary.Routes![0].Via.Should().Be("10.0.0.254");
        primary.Routes![0].Metric.Should().Be(100);
        primary.Routes![1].To.Should().Be("default");
        primary.Mtu.Should().Be(9000);
        primary.MacAddress.Should().Be("00:11:22:33:44:55");

        result.Ethernets!["fallback"].Dhcp4.Should().BeTrue();
        result.Bonds.Should().ContainKey("bond0");
        result.Bridges.Should().ContainKey("br0");
        result.Vlans.Should().ContainKey("vlan100");
    }

    // ----- YAML syntax robustness (cross-model review gaps) -----

    [Fact]
    public void Deserialize_v2_anchors_and_merge_keys_are_expanded()
    {
        // netplan factors shared interface settings out with a YAML anchor and
        // pulls them in via a `<<:` merge key. The serializer wraps input in a
        // MergingParser specifically for this; prove it for network-config.
        const string yaml = """
                            version: 2
                            ethernets:
                              _base: &base
                                dhcp4: true
                                mtu: 1500
                                nameservers:
                                  addresses: [1.1.1.1]
                              eth0:
                                <<: *base
                                match:
                                  macaddress: "00:11:22:33:44:55"
                                addresses: [10.0.0.5/24]
                            """;

        var result = NetworkConfigYamlSerializer.Deserialize(yaml);

        var eth0 = result.Ethernets!["eth0"];
        eth0.Dhcp4.Should().BeTrue();                 // merged from anchor
        eth0.Mtu.Should().Be(1500);                   // merged from anchor
        eth0.Nameservers!.Addresses.Should().BeEquivalentTo(["1.1.1.1"]); // merged
        eth0.Match!.MacAddress.Should().Be("00:11:22:33:44:55");          // local
        eth0.Addresses.Should().BeEquivalentTo(["10.0.0.5/24"]);          // local
    }

    [Fact]
    public void Deserialize_v2_unquoted_mac_in_match_and_top_level()
    {
        // Hand-written netplan and several generators emit MACs WITHOUT quotes.
        // A MAC is a plain scalar (no colon-space), and MAC/macaddress are
        // string targets so the YAML 1.1 resolver never coerces them.
        const string yaml = """
                            version: 2
                            ethernets:
                              eth0:
                                match:
                                  macaddress: 00:11:22:aa:bb:cc
                                dhcp4: true
                              eth1:
                                macaddress: 00:00:00:00:00:12
                                dhcp4: true
                            """;

        var result = NetworkConfigYamlSerializer.Deserialize(yaml);

        result.Ethernets!["eth0"].Match!.MacAddress.Should().Be("00:11:22:aa:bb:cc");
        // An all-decimal-looking MAC must not be mangled into a number.
        result.Ethernets!["eth1"].MacAddress.Should().Be("00:00:00:00:00:12");
    }

    [Fact]
    public void Deserialize_v2_unquoted_ipv6_addresses_and_gateway()
    {
        const string yaml = """
                            version: 2
                            ethernets:
                              eth0:
                                macaddress: "00:11:22:33:44:55"
                                addresses:
                                  - fd00::5/64
                                gateway6: fd00::1
                                nameservers:
                                  addresses:
                                    - 2606:4700:4700::1111
                            """;

        var result = NetworkConfigYamlSerializer.Deserialize(yaml);

        var eth0 = result.Ethernets!["eth0"];
        eth0.Addresses.Should().BeEquivalentTo(["fd00::5/64"]);
        eth0.Gateway6.Should().Be("fd00::1");
        eth0.Nameservers!.Addresses.Should().BeEquivalentTo(["2606:4700:4700::1111"]);
    }

    [Fact]
    public void Deserialize_v2_quoted_bool_tokens_still_parse_as_bool()
    {
        // dhcp4/dhcp6 are bool? targets; the YAML 1.1 resolver accepts the bool
        // token whether plain or quoted (cloud-init parity), so "no"/"yes" parse.
        const string yaml = """
                            version: 2
                            ethernets:
                              eth0:
                                macaddress: "00:11:22:33:44:55"
                                dhcp4: "no"
                                dhcp6: "yes"
                            """;

        var result = NetworkConfigYamlSerializer.Deserialize(yaml);

        result.Ethernets!["eth0"].Dhcp4.Should().BeFalse();
        result.Ethernets!["eth0"].Dhcp6.Should().BeTrue();
    }

    [Fact]
    public void Deserialize_v2_addresses_empty_list_yields_empty()
    {
        const string yaml = """
                            version: 2
                            ethernets:
                              eth0:
                                macaddress: "00:11:22:33:44:55"
                                addresses: []
                            """;

        var result = NetworkConfigYamlSerializer.Deserialize(yaml);

        result.Ethernets!["eth0"].Addresses.Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public void Deserialize_v2_addresses_explicit_null_yields_empty()
    {
        const string yaml = """
                            version: 2
                            ethernets:
                              eth0:
                                macaddress: "00:11:22:33:44:55"
                                addresses: ~
                            """;

        var result = NetworkConfigYamlSerializer.Deserialize(yaml);

        result.Ethernets!["eth0"].Addresses.Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public void Deserialize_v2_addresses_lone_scalar_is_promoted_to_list()
    {
        // Some old generators emit a single address as a scalar, not a list.
        const string yaml = """
                            version: 2
                            ethernets:
                              eth0:
                                macaddress: "00:11:22:33:44:55"
                                addresses: 10.0.0.5/24
                            """;

        var result = NetworkConfigYamlSerializer.Deserialize(yaml);

        result.Ethernets!["eth0"].Addresses.Should().BeEquivalentTo(["10.0.0.5/24"]);
    }

    [Fact]
    public void Deserialize_v2_addresses_null_entry_in_list_is_dropped()
    {
        const string yaml = """
                            version: 2
                            ethernets:
                              eth0:
                                macaddress: "00:11:22:33:44:55"
                                addresses:
                                  - 10.0.0.5/24
                                  - ~
                                  - fd00::1/64
                            """;

        var result = NetworkConfigYamlSerializer.Deserialize(yaml);

        result.Ethernets!["eth0"].Addresses
            .Should().BeEquivalentTo(["10.0.0.5/24", "fd00::1/64"]);
    }

    [Fact]
    public void Deserialize_v2_advanced_address_map_empty_and_null_values()
    {
        // The advanced map form with an empty `{}` or a null value still yields
        // just the address (the map key); it must not throw.
        const string yaml = """
                            version: 2
                            ethernets:
                              eth0:
                                macaddress: "00:11:22:33:44:55"
                                addresses:
                                  - "10.0.0.5/24": {}
                                  - "10.0.0.6/24":
                            """;

        var result = NetworkConfigYamlSerializer.Deserialize(yaml);

        result.Ethernets!["eth0"].Addresses
            .Should().BeEquivalentTo(["10.0.0.5/24", "10.0.0.6/24"]);
    }

    [Fact]
    public void Deserialize_v2_advanced_address_flow_map_item()
    {
        // Flow-form of the advanced map item, as MAAS/cloud-init tooling emits.
        const string yaml = """
                            version: 2
                            ethernets:
                              eth0:
                                macaddress: "00:11:22:33:44:55"
                                addresses:
                                  - 10.0.0.5/24
                                  - {"fd00::5/64": {lifetime: 0}}
                            """;

        var result = NetworkConfigYamlSerializer.Deserialize(yaml);

        result.Ethernets!["eth0"].Addresses
            .Should().BeEquivalentTo(["10.0.0.5/24", "fd00::5/64"]);
    }

    [Fact]
    public void Deserialize_v2_advanced_address_multi_key_map_keeps_first_strict()
    {
        // A multi-key map item does not occur in netplan; we deliberately keep
        // only the first key (strict). This pins that behavior so it can't
        // change silently.
        const string yaml = """
                            version: 2
                            ethernets:
                              eth0:
                                macaddress: "00:11:22:33:44:55"
                                addresses:
                                  - "10.0.0.5/24": {label: a}
                                    "10.0.0.6/24": {label: b}
                            """;

        var result = NetworkConfigYamlSerializer.Deserialize(yaml);

        result.Ethernets!["eth0"].Addresses.Should().BeEquivalentTo(["10.0.0.5/24"]);
    }

    [Fact]
    public void Deserialize_v2_address_map_with_non_scalar_key_does_not_throw()
    {
        // YAML permits a non-scalar mapping key. The converter must drain it
        // rather than throw (its "never throw on valid YAML" goal); such a key
        // carries no address, and a normal entry alongside it still parses.
        const string yaml = """
                            version: 2
                            ethernets:
                              eth0:
                                macaddress: "00:11:22:33:44:55"
                                addresses:
                                  - {[a, b]: 0}
                                  - 10.0.0.5/24
                            """;

        var result = NetworkConfigYamlSerializer.Deserialize(yaml);

        result.Ethernets!["eth0"].Addresses.Should().BeEquivalentTo(["10.0.0.5/24"]);
    }

    [Fact]
    public void Deserialize_v2_with_comments_and_crlf_line_endings()
    {
        // Windows-authored configs use CRLF and operators add comments.
        var yaml =
            "version: 2\r\n" +
            "ethernets:\r\n" +
            "  eth0:  # primary nic\r\n" +
            "    macaddress: \"00:11:22:33:44:55\"\r\n" +
            "    # static address follows\r\n" +
            "    addresses: [10.0.0.5/24]\r\n";

        var result = NetworkConfigYamlSerializer.Deserialize(yaml);

        var eth0 = result.Ethernets!["eth0"];
        eth0.MacAddress.Should().Be("00:11:22:33:44:55");
        eth0.Addresses.Should().BeEquivalentTo(["10.0.0.5/24"]);
    }

    [Fact]
    public void Deserialize_v2_leading_utf8_bom_is_stripped()
    {
        // A UTF-8 BOM from a Windows editor must not break the parse.
        var yaml =
            "\uFEFF" +
            "version: 2\n" +
            "ethernets:\n" +
            "  eth0:\n" +
            "    macaddress: \"00:11:22:33:44:55\"\n" +
            "    dhcp4: true\n";

        var result = NetworkConfigYamlSerializer.Deserialize(yaml);

        result.Version.Should().Be(2);
        result.Ethernets!["eth0"].Dhcp4.Should().BeTrue();
    }

    [Fact]
    public void Deserialize_v2_mac_address_underscore_form_does_not_bind_strict()
    {
        // v2 spells the selector `macaddress` (no underscore); `mac_address`
        // is the v1 spelling. In a v2 ethernet it is not bound — strict, matching
        // netplan. Pinned so the alias behavior can't regress silently.
        const string yaml = """
                            version: 2
                            ethernets:
                              eth0:
                                mac_address: "00:11:22:33:44:55"
                                dhcp4: true
                              eth1:
                                match:
                                  mac_address: "00:11:22:33:44:66"
                                dhcp4: true
                            """;

        var result = NetworkConfigYamlSerializer.Deserialize(yaml);

        result.Ethernets!["eth0"].MacAddress.Should().BeNull();
        result.Ethernets!["eth1"].Match.Should().BeNull();
    }

    [Fact]
    public void Deserialize_v2_wrapper_takes_precedence_over_root_keys_strict()
    {
        // A malformed doc carrying BOTH a top-level ethernet and a `network:`
        // wrapper: the wrapper wins and the root-level entry is dropped. Pin
        // this precedence (strict) so it's intentional, not accidental.
        const string yaml = """
                            version: 2
                            ethernets:
                              rootonly:
                                macaddress: "00:11:22:33:44:01"
                                dhcp4: true
                            network:
                              version: 2
                              ethernets:
                                wrapped:
                                  macaddress: "00:11:22:33:44:02"
                                  dhcp4: true
                            """;

        var result = NetworkConfigYamlSerializer.Deserialize(yaml);

        result.Ethernets.Should().ContainKey("wrapped");
        result.Ethernets.Should().NotContainKey("rootonly");
    }

    [Fact]
    public void Deserialize_v1_physical_with_dhcp_subnet_projects_to_ethernets()
    {
        // v1 has a 'config' list rather than top-level ethernets/..; physical
        // entries are projected into the v2-shape Ethernets dictionary keyed
        // by name so a single applier can handle both schemas.
        const string yaml = """
                            version: 1
                            config:
                              - type: physical
                                name: eth0
                                mac_address: d2:ab:04:5a:29:47
                                subnets:
                                  - type: dhcp
                            """;

        var result = NetworkConfigYamlSerializer.Deserialize(yaml);

        result.Version.Should().Be(1);
        result.Ethernets.Should().ContainKey("eth0");
        var eth0 = result.Ethernets!["eth0"];
        eth0.Dhcp4.Should().BeTrue();
        eth0.MacAddress.Should().Be("d2:ab:04:5a:29:47");
        eth0.Addresses.Should().BeNull();
    }

    [Fact]
    public void Deserialize_v1_physical_with_static_subnet_and_dns()
    {
        const string yaml = """
                            version: 1
                            config:
                              - type: physical
                                name: eth0
                                mac_address: 00:11:22:33:44:55
                                mtu: 1400
                                subnets:
                                  - type: static
                                    address: 10.0.0.5/24
                                    gateway: 10.0.0.1
                                    dns_nameservers:
                                      - 1.1.1.1
                                    dns_search:
                                      - corp.local
                            """;

        var result = NetworkConfigYamlSerializer.Deserialize(yaml);

        result.Version.Should().Be(1);
        var eth0 = result.Ethernets!["eth0"];
        eth0.Dhcp4.Should().BeNull();
        eth0.Addresses.Should().BeEquivalentTo(["10.0.0.5/24"]);
        eth0.Gateway4.Should().Be("10.0.0.1");
        eth0.Mtu.Should().Be(1400);
        eth0.Nameservers!.Addresses.Should().BeEquivalentTo(["1.1.1.1"]);
        eth0.Nameservers!.Search.Should().BeEquivalentTo(["corp.local"]);
    }

    [Fact]
    public void Deserialize_v1_global_nameserver_entry_is_inherited_by_physicals()
    {
        // Cloud-init v1: a top-level type=nameserver entry supplies DNS that
        // applies to physical adapters that don't carry their own.
        const string yaml = """
                            version: 1
                            config:
                              - type: physical
                                name: eth0
                                subnets:
                                  - type: static
                                    address: 10.0.0.5/24
                              - type: nameserver
                                address:
                                  - 9.9.9.9
                                search:
                                  - corp.local
                            """;

        var result = NetworkConfigYamlSerializer.Deserialize(yaml);

        var eth0 = result.Ethernets!["eth0"];
        eth0.Nameservers!.Addresses.Should().BeEquivalentTo(["9.9.9.9"]);
        eth0.Nameservers!.Search.Should().BeEquivalentTo(["corp.local"]);
    }

    [Fact]
    public void Deserialize_unknown_renderer_falls_back_to_other()
    {
        const string yaml = """
                            version: 2
                            renderer: some-future-renderer
                            """;

        var result = NetworkConfigYamlSerializer.Deserialize(yaml);

        result.Renderer.Should().Be(NetworkDhcpRenderer.Other);
    }
}
