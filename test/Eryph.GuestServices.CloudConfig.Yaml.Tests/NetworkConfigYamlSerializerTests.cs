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
